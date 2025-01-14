﻿#nowarn "9"
#nowarn "1337"
namespace Aardvark.Service

open System
open System.Text
open System.Net
open System.Threading
open System.Collections.Concurrent

open Suave
open Suave.Http
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.Files
open Suave.RequestErrors
open Suave.Logging
open Suave.Utils
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket

open Suave.Extensions


open Aardvark.Base
open Aardvark.Rendering
open Aardvark.GPGPU
open FSharp.Data.Adaptive
open Aardvark.Application
open System.Diagnostics

open Internals

type Message =
    | RequestImage of background : C4b * size : V2i
    | RequestWorldPosition of pixel : V2i
    | Rendered
    | Shutdown
    | Change of scene : string * samples : int


type internal ClientCreateInfo =
    {
        server          : Server
        session         : Guid
        id              : string
        sceneName       : string
        samples         : int
        socket          : WebSocket
        useMapping      : bool
        getSignature    : int -> IFramebufferSignature
        targetQuality   : RenderQuality
    }

type private DummyObject() =
    inherit AdaptiveObject()

type internal Client(updateLock : obj, createInfo : ClientCreateInfo, getState : ClientInfo -> ClientState, getContent : IFramebufferSignature -> string -> ConcreteScene) as this =
    static let mutable currentId = 0
 
    static let newTask (info : ClientCreateInfo) getContent =
        if info.useMapping then
            new MappedClientRenderTask(info.server, getContent) :> ClientRenderTask
        else
            new JpegClientRenderTask(info.server, getContent, info.targetQuality) :> ClientRenderTask

    let id = Interlocked.Increment(&currentId)
    let sender = DummyObject()
    let requestedSize : MVar<C4b * V2i> = MVar.empty()
    let mutable createInfo = createInfo
    let mutable task = newTask createInfo getContent
    let mutable running = false
    let mutable disposed = 0

    let mutable frameCount = 0
    let roundTripTime = Stopwatch()
    let invalidateTime = Stopwatch()

    let send (cmd : Command) =
        let data = Pickler.json.Pickle cmd

        let res = createInfo.socket.send Opcode.Text (ByteSegment data) true |> Async.RunSynchronously
        match res with
            | Choice1Of2 () -> ()
            | Choice2Of2 err ->
                Log.warn "[Client] %d: send of %A faulted (stopping): %A" id cmd err
                this.Dispose()
                //failwithf "[Client] %d: %A" id err

    let subscribe() =
        sender.AddMarkingCallback(fun () ->
            invalidateTime.Start()
            send Invalidate
        )

    let mutable info =
        {
            token = Unchecked.defaultof<AdaptiveToken>
            signature = createInfo.getSignature createInfo.samples
            targetId = createInfo.id
            sceneName = createInfo.sceneName
            session = createInfo.session
            samples = createInfo.samples
            quality = createInfo.targetQuality
            size = V2i.II
            time = MicroTime.Now
            clearColor = C4f.Black
        }

    let mutable subscription = subscribe()


    let renderLoop() =
        while running do
            let (background, size) = MVar.take requestedSize
            if size.AllGreater 0 then
                lock updateLock (fun () ->
                    sender.EvaluateAlways AdaptiveToken.Top (fun token ->
                        let info = Interlocked.Change(&info, fun info -> { info with token = token; size = size; time = MicroTime.Now; clearColor = background.ToC4f() })
                        try
                            let state = getState info
                            let data = task.Run(token, info, state)
                            match data with
                                | Jpeg data -> 
                                    let res = createInfo.socket.send Opcode.Binary (ByteSegment data) true |> Async.RunSynchronously
                                    match res with
                                        | Choice1Of2() -> ()
                                        | Choice2Of2 err ->
                                            running <- false
                                            Log.warn "[Client] %d: could not send render-result due to %A (stopping)" id err
                                | Png data -> 
                                    Log.error "[Client] %d: requested png render control which is not supported at the moment (png conversion to slow)" id
                                
                                | Mapping img ->
                                    let data = Pickler.json.Pickle img
                                    let res = createInfo.socket.send Opcode.Text (ByteSegment data) true |> Async.RunSynchronously
                                    match res with
                                        | Choice1Of2() -> ()
                                        | Choice2Of2 err ->
                                            running <- false
                                            Log.warn "[Client] %d: could not send render-result due to %A (stopping)" id err
                                    
                        with e ->
                            running <- false
                            Log.error "[Client] %d: rendering faulted with %A (stopping)" id e
                    
                    )
                )

                


    let mutable renderThread = new Thread(ThreadStart(renderLoop), IsBackground = true, Name = "ClientRenderer_" + string createInfo.session)


    member x.Info = info
    member x.FrameCount = frameCount
    member x.FrameTime = roundTripTime.MicroTime
    member x.InvalidateTime = invalidateTime.MicroTime
    member x.RenderTime = task.RenderTime
    member x.CompressTime = task.CompressTime


    member x.Revive(newInfo : ClientCreateInfo) =
        if Interlocked.Exchange(&disposed, 0) = 1 then
            Log.line "[Client] %d: revived" id
            createInfo <- newInfo
            task <- newTask newInfo getContent
            subscription <- subscribe()
            renderThread <- new Thread(ThreadStart(renderLoop), IsBackground = true, Name = "ClientRenderer_" + string info.session)

    member x.Dispose() =
        if Interlocked.Exchange(&disposed, 1) = 0 then
            task.Dispose()
            subscription.Dispose()
            running <- false
            MVar.put requestedSize (C4b.Black, V2i.Zero)
            frameCount <- 0
            roundTripTime.Reset()
            invalidateTime.Reset()

    member x.Run =
        running <- true
        renderThread.Start()
        socket {
            
            Log.line "[Client] %d: running %s" id info.sceneName
            try
                while running do
                    let! (code, data) = createInfo.socket.readMessage()

                    match code with
                        | Opcode.Text ->
                            try
                                
                                if data.Length > 0 && data.[0] = uint8 '#' then
                                    let str = System.Text.Encoding.UTF8.GetString(data, 1, data.Length - 1)
                                    match str with
                                        | "ping" -> 
                                            do! createInfo.socket.send Opcode.Pong (ByteSegment([||])) true
                                        | _ ->
                                            Log.warn "bad opcode: %A" str
                                else
                                    let msg : Message = Pickler.json.UnPickle data

                                    match msg with
                                        | RequestImage(background, size) ->
                                            invalidateTime.Stop()
                                            roundTripTime.Start()
                                            MVar.put requestedSize (background, size)

                                        | RequestWorldPosition pixel ->
                                            let wp = 
                                                match task.GetWorldPosition pixel with
                                                    | Some d -> d
                                                    | None -> V3d.Zero

                                            send (WorldPosition wp)

                                        | Rendered ->
                                            roundTripTime.Stop()
                                            frameCount <- frameCount + 1

                                        | Shutdown ->
                                            running <- false

                                        | Change(scene, samples) ->
                                            let signature = createInfo.getSignature samples
                                            Interlocked.Change(&info, fun info -> { info with samples = samples; signature = signature; sceneName = scene }) |> ignore

                            with e ->
                                Log.warn "[Client] %d: unexpected message %A" id (Encoding.UTF8.GetString data)

                        | Opcode.Binary ->
                            Log.warn "[Client] %d: unexpected binary message" id
                        | Opcode.Close ->
                            running <- false
                        | Opcode.Ping -> 
                            do! createInfo.socket.send Opcode.Pong (ByteSegment([||])) true
                        | _ ->
                            ()

            finally
                Log.line "[Client] %d: stopped" id
                x.Dispose()
        }

    interface IDisposable with
        member x.Dispose() = x.Dispose()





[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Server =
    open System.IO
    open System.Collections.Generic
    open System.Collections.Concurrent
    open System.Runtime.CompilerServices

    [<AutoOpen>]
    module private Utils = 
        type ClientStatistics =
            {
                session         : Guid
                name            : string
                frameCount      : int
                invalidateTime  : float
                renderTime      : float
                compressTime    : float
                frameTime       : float
            }

        type Client with
            member internal x.GetStatistics() =
                {
                    session = x.Info.session
                    name = x.Info.sceneName
                    frameCount = x.FrameCount
                    invalidateTime = x.InvalidateTime.TotalSeconds
                    renderTime = x.RenderTime.TotalSeconds
                    compressTime = x.CompressTime.TotalSeconds
                    frameTime = x.FrameTime.TotalSeconds
                }

        let (|Int|_|) (str : string) =
            match Int32.TryParse str with
                | (true, v) -> Some v
                | _ -> None

        let (|C4f|_|) (str : string) =
            try Some (C4f.Parse str) with e -> None

        let noState =
            {
                viewTrafo = Trafo3d.Identity
                projTrafo = Trafo3d.Identity
            }

    let empty (useGpuCompression : bool) (r : IRuntime) =
        let compressor =
            if useGpuCompression then new JpegCompressor(r) |> Some
            else None
        {
            runtime = r
            rendered = fun _ -> ()
            content = fun _ -> None
            getState = fun _ -> None
            compressor = compressor
            fileSystemRoot = None
        }

    let withState (get : ClientInfo -> ClientState) (server : Server) =
        { server with getState = get >> Some }

    let withContent (get : string -> Option<Scene>) (server : Server) =
        { server with content = get }

    let addScenes (find : string -> Option<Scene>) (server : Server) =
        { 
            server with
                content = fun n ->
                    match find n with
                        | Some s -> Some s
                        | None -> server.content n
        }

    let addScene (name : string) (scene : Scene) (server : Server) =
        { 
            server with
                content = fun n ->
                    if n = name then Some scene
                    else server.content n
        }

    let add (name : string) (create : ClientValues -> IRenderTask) (server : Server) =
        addScene name (Scene.custom create) server

    let toWebPart (updateLock : obj) (info : Server) =
        let clients = Dict<Guid * string, Client>()

        let signatures = ConcurrentDictionary<int, IFramebufferSignature>()

        let getSignature (samples : int) =
            signatures.GetOrAdd(samples, fun samples ->
                info.runtime.CreateFramebufferSignature(
                    [
                        DefaultSemantic.Colors, TextureFormat.Rgba8
                        DefaultSemantic.DepthStencil, TextureFormat.Depth24Stencil8
                    ],
                    samples
                )
            )

        let getState (ci : ClientInfo) =
            match info.getState ci with
                | Some c -> c
                | None -> noState



        let content signature id  =
            match info.content id with   
                | Some scene -> scene.GetConcreteScene(id, signature)
                | None -> Scene.empty.GetConcreteScene(id, signature)

        let render (targetId : string) (ws : WebSocket) (context: HttpContext) =
            let request = context.request
            let args = request.query |> List.choose (function (n,Some v) -> Some(n,v) | _ -> None) |> Map.ofList
            
            let sceneName =
                match Map.tryFind "scene" args with
                    | Some scene -> scene
                    | _ -> targetId

            let samples =
                match Map.tryFind "samples" args with
                    | Some (Int samples) -> samples
                    | _ -> 1

            let sessionId =
                match Map.tryFind "session" args with
                    | Some id -> Guid.Parse id
                    | _ -> Guid.NewGuid()

            let useMapping =
                match Map.tryFind "mapped" args with
                    | Some "false" -> false
                    | Some "true" -> true
                    | _ -> false

            let quality =
                match Map.tryFind "quality" args with
                    | Some q -> 
                        match Int32.TryParse q with
                            | (true,v) when v >=1 && v <= 100 -> float v
                            | _ -> 
                                Log.warn "could not parse quality. should be int of range [1,100]"
                                RenderQuality.full.quality
                    | _ -> RenderQuality.full.quality

            let createInfo =
                {
                    server          = info
                    session         = sessionId
                    id              = targetId
                    sceneName       = sceneName
                    samples         = samples
                    targetQuality   = { RenderQuality.full with quality = quality }
                    socket          = ws
                    useMapping      = useMapping
                    getSignature    = getSignature
                }            

            let client = 
                let key = (sessionId, targetId)
                lock clients (fun () ->
                    clients.GetOrCreate(key, fun (sessionId, targetId) ->
                        Log.line "[Server] created client for (%A/%s), mapping %s" sessionId targetId (if useMapping then "enabled" else "disabled")
                        new Client(updateLock, createInfo, getState, content)
                    )
                )

            client.Revive(createInfo)
            client.Run

        let statistics (ctx : HttpContext) =
            let request = ctx.request
            let args = request.query |> List.choose (function (n,Some v) -> Some(n,v) | _ -> None) |> Map.ofList

            let clients = 
                match Map.tryFind "session" args, Map.tryFind "name" args with
                    | Some sid, Some name -> 
                        match Guid.TryParse sid with
                            | (true, sid) -> 
                                match clients.TryGetValue((sid, name)) with
                                    | (true, c) -> [| c |]
                                    | _ -> [||]
                            | _ ->
                                [||]
                    | _ -> 
                        clients.Values |> Seq.toArray

            let stats = clients  |> Array.filter (fun v -> not (isNull (v :> obj))) |> Array.map (fun c -> c.GetStatistics()) |> Array.filter (fun s -> s.frameCount > 0)
            let json = Pickler.json.PickleToString stats
            ctx |> (OK json >=> Writers.setMimeType "text/json")

        let  screenshot' (sceneName : string) (context: HttpContext) =
            let request = context.request
            let args = request.query |> List.choose (function (n,Some v) -> Some(n,v) | _ -> None) |> Map.ofList

            let samples = 
                match Map.tryFind "samples" args with
                    | Some (Int s) -> s
                    | _ -> 1

            let signature = getSignature samples

            match Map.tryFind "w" args, Map.tryFind "h" args with
                | Some (Int w), Some (Int h) when w > 0 && h > 0 ->
                    let scene = content signature sceneName

                    let clearColor = 
                        match Map.tryFind "background" args with // fmt: C4f.Parse("[1.0,2.0,0.2,0.2]") 
                            | Some (C4f c) -> c
                            | Some bg -> 
                                Log.warn "[render service] could not parse background color: %s (format should be e.g. [1.0,2.0,0.2,0.2])" bg
                                C4f.Black
                            | None -> C4f.Black

                    let clientInfo = 
                        {
                            token = AdaptiveToken.Top
                            signature = signature
                            targetId = ""
                            sceneName = sceneName
                            session = Guid.Empty
                            size = V2i(w,h)
                            samples = samples
                            time = MicroTime.Now
                            clearColor = clearColor
                            quality = RenderQuality.full
                        }

                    let state = getState clientInfo

                    let respondOK (mime : string) (task : ClientRenderTask)  =
                        let data = 
                            match task.Run(AdaptiveToken.Top, clientInfo, state) with
                                | RenderResult.Jpeg d -> d
                                | RenderResult.Png d -> d
                                | _ -> failwith "that was unexpected"

                        context |> (ok data >=> Writers.setMimeType mime)

                    match Map.tryFind "fmt" args with
                        | Some "jpg" | None -> 
                            use t = new JpegClientRenderTask(info, content, RenderQuality.full) :> ClientRenderTask
                            t |> respondOK "image/jpeg"
                        | Some "png" -> 
                            use t = new PngClientRenderTask(info, content) :> ClientRenderTask
                            t |> respondOK "image/png"
                        | Some fmt -> context |> BAD_REQUEST (sprintf  "format not supported: %s" fmt)

                | _ ->
                    context |> BAD_REQUEST "no width/height specified"

        
        let screenshot (sceneName : string) (context: HttpContext) =
            try 
                screenshot' sceneName context
            with e -> 
                Log.warn "[Media/Screenshot] failed with: %A" e
                context |> BAD_REQUEST (sprintf "screenshot failed with %s" e.Message)
        
        choose [
            yield Reflection.assemblyWebPart typeof<Client>.Assembly
            yield pathScan "/render/%s" (render >> handShake)
            yield path "/stats.json" >=> statistics 
            yield pathScan "/screenshot/%s" screenshot

            match info.fileSystemRoot with
                | Some root ->
                    let fileSystem = 
                        if root = "/" then new FileSystem()
                        else new FileSystem(root)
                    yield path "/fs" >=> FileSystem.toWebPart fileSystem
                | None ->
                    ()
        ]

    let run (port : int) (server : Server) =
        server |> toWebPart (obj())  |> List.singleton |> WebPart.runServer port

    let start (port : int) (server : Server) =
        server |> toWebPart (obj())  |> List.singleton |> WebPart.startServer port
    
    // c# friendly to start app directly
    let StartWebPart (port:int) (webPart:WebPart) =
        webPart |> List.singleton |> WebPart.startServer port