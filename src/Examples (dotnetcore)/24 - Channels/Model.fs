namespace RenderControl.Model

open Aardvark.Base
open FSharp.Data.Adaptive
open Aardvark.UI.Primitives

type Message = 
    | Camera of FreeFlyController.Message
    | CenterScene
    | SetFiles of list<string>

[<ModelType>]
type Model = 
    {
        cameraState : CameraControllerState
    }