﻿namespace Aardvark.UI.Anewmation

open Aardvark.Base

[<AutoOpen>]
module AnimationSplinePrimitives =

    module Splines =

        type CatmullRom<'T>(distance : ^T -> ^T -> float, evaluate : float -> 'T, samples : int) =
            let tj = Array.init samples (fun i -> float i / float (samples - 1))
            let pj = tj |> Array.map evaluate
            let sj = (0.0, Array.pairwise pj) ||> Array.scan (fun d (a, b) -> d + distance a b)

            let length = Array.last sj
            do for i in 0 .. sj.Length - 1 do
                sj.[i] <- sj.[i] / length

            let segments =
                Array.init (sj.Length - 1) (fun i ->
                    struct {| Start = sj.[i]; End = sj.[i + 1] |}
                )

            let lookup s =
                let i =
                    if s < 0.0 then 0
                    elif s > 1.0 then segments.Length - 1
                    else
                        segments |> Array.binarySearch (fun segment ->
                            if s < segment.Start then -1 elif s > segment.End then 1 else 0
                        ) |> ValueOption.get

                let a = segments.[i].Start
                let b = segments.[i].End
                let t = (s - a) / (b - a)

                tj.[i] + t * (tj.[i + 1] - tj.[i])

            member x.Length = length
            member x.Evaluate(s) = s |> lookup |> evaluate


        let inline catmullRom (distance : ^T -> ^T -> float) (points : ^T[]) =

            let scale (t : float) (x : ^T) =
                t |> lerp zero x

            // Evaluation of a single segment (4 control points)
            let segment (tj : float[]) (pj : ^T[]) (index : int) =
                let t0 = tj.[index]
                let t1 = tj.[index + 1]
                let t2 = tj.[index + 2]
                let t3 = tj.[index + 3]

                let evaluate t =
                    let t = t1 + t * (t2 - t1)
                    let a1 = scale ((t1 - t) / (t1 - t0)) pj.[index + 0] + scale ((t - t0) / (t1 - t0)) pj.[index + 1]
                    let a2 = scale ((t2 - t) / (t2 - t1)) pj.[index + 1] + scale ((t - t1) / (t2 - t1)) pj.[index + 2]
                    let a3 = scale ((t3 - t) / (t3 - t2)) pj.[index + 2] + scale ((t - t2) / (t3 - t2)) pj.[index + 3]
                    let b1 = scale ((t2 - t) / (t2 - t0)) a1 + scale ((t - t0) / (t2 - t0)) a2
                    let b2 = scale ((t3 - t) / (t3 - t1)) a2 + scale ((t - t1) / (t3 - t1)) a3
                    scale ((t2 - t) / (t2 - t1)) b1 + scale ((t - t1) / (t2 - t1)) b2

                CatmullRom(distance, evaluate, 32)


            if Array.isEmpty points then
                Array.empty
            else
                let tj = Array.zeroCreate (points.Length + 2)
                let pj = Array.zeroCreate (points.Length + 2)

                pj.[1] <- points.[0]
                tj.[1] <- LanguagePrimitives.GenericZero

                let mutable n = 2

                for i in 1 .. points.Length - 1 do
                    let d = sqrt (distance pj.[n - 1] points.[i])
                    if d > LanguagePrimitives.GenericZero then
                        pj.[n] <- points.[i]
                        tj.[n] <- tj.[n - 1] + d
                        inc &n
                    else
                        Log.warn "[Animation] Ignoring duplicate control point in spline"

                // At this point n is the number of control points + 1, or number of final points
                // minus 1 since we compute and add a point to each end.
                if n = 2 then
                    [| CatmullRom((fun _ _ -> 0.0), (fun _ -> pj.[1]), 1) |]
                else
                    pj.[0] <- scale 2.0 (pj.[1] - pj.[2])
                    tj.[0] <- LanguagePrimitives.GenericZero

                    pj.[n] <- scale 2.0 (pj.[n - 1] - pj.[n - 2])
                    tj.[n] <- sqrt (distance pj.[n - 1] pj.[n])

                    let d = sqrt (distance pj.[0] pj.[1])
                    for i in 1 .. n do
                        tj.[i] <- tj.[i] + d

                    Array.init (n - 2) (segment tj pj)


    module Animation =

        module Primitives =

            /// Creates an array of animations that smoothly interpolate along the path given by the control points.
            /// The animations are scaled according to the distance between the points. Coinciding points are ignored.
            let inline smoothPath' (distance : ^Value -> ^Value -> float) (points : ^Value seq) : IAnimation<'Model, ^Value>[] =

                let points = Array.ofSeq points
                let spline = points |> Splines.catmullRom distance
                let maxLength = spline |> Array.map (fun s -> s.Length) |> Array.max

                spline |> Array.map (fun s ->
                    Animation.create s.Evaluate
                    |> Animation.seconds (s.Length / maxLength)
                )

            /// Creates an animation that smoothly interpolates along the path given by the control points. Coinciding points are ignored.
            let inline smoothPath (distance : ^Value -> ^Value -> float) (points : ^Value seq) : IAnimation<'Model, ^Value> =
                points |> smoothPath' distance |> Animation.path