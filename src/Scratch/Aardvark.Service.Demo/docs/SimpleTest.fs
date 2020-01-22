module SimpleTestApp

open SimpleTest
open Aardvark.Base
open FSharp.Data.Adaptive

open Aardvark.SceneGraph
open Aardvark.Base.Rendering
open Aardvark.UI
open Aardvark.UI.Primitives

type Action = 
    | Inc 
    | Dec
    | CameraAction of CameraController.Message

let update (m : Model) (a : Action) =
    match a with
        | Inc -> { m with value = m.value + 1.0 }
        | Dec -> { m with value = m.value - 1.0 } 
        | CameraAction a -> { m with cameraModel = CameraController.update m.cameraModel a }

let cam = 
    Camera.create (CameraView.lookAt (V3d.III * 3.0) V3d.OOO V3d.OOI) (Frustum.perspective 60.0 0.1 10.0 1.0)


let threeD (m : MModel) =

    let t =
        adaptive {
            let! t = m.value
            return Trafo3d.RotationZ(t * 0.1)
        }

    let sg =
        Sg.box (AVal.constant C4b.Green) (AVal.constant Box3d.Unit)
        |> Sg.requirePicking
        |> Sg.noEvents
        //|> Sg.pickable (PickShape.Box Box3d.Unit)       
        |> Sg.trafo t
        |> Sg.withEvents [
                Sg.onMouseDown (fun _ _ -> Inc)
          ]
        |> Sg.effect [
                    toEffect DefaultSurfaces.trafo
                    //toEffect DefaultSurfaces.diffuseTexture
                    toEffect <| DefaultSurfaces.constantColor C4f.Red 
                ]
            

    let frustum = Frustum.perspective 60.0 0.1 100.0 1.0
    CameraController.controlledControl m.cameraModel CameraAction
        (AVal.constant frustum) 
        (AttributeMap.ofList [ attribute "style" "width:70%; height: 100%"]) sg

let view (m : MModel) =
    let s =
        adaptive {
            let! v = m.value
            return string v
        }
    printfn "exectued some things..."
    div [] [
        text "constant text"
        br []
        Incremental.text s
        //text (AVal.force s)
        br []
        button [onMouseClick (fun _ -> Inc)] [text "inc"]
        button [onMouseClick (fun _ -> Dec)] [text "dec"]
        br []
        threeD m
    ]

let app =
    {
        unpersist = Unpersist.instance
        threads = fun (model : Model) -> CameraController.threads model.cameraModel |> ThreadPool.map CameraAction
        initial = { value = 1.0; cameraModel = CameraController.initial }
        update = update
        view = view
    }

let start() = App.start app