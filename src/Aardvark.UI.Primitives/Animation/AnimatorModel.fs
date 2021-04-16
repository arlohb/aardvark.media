﻿namespace Aardvark.UI.Anewmation

open Aardvark.Base
open FSharp.Data.Adaptive

type Animator<'Model> =
    {
        Slots : HashMap<Symbol, AnimatorSlot<'Model>>
        TickRate : int
        mutable TickCount : int
    }

[<RequireQualifiedAccess>]
type AnimatorMessage<'Model> =
    /// Performs an animation tick.
    | Tick

    /// Creates an animation instance for the slot with the given name, and performs the given action.
    /// Replaces any existing instances (current and queued) in the given slot.
    | Set     of name: Symbol * animation: IAnimation<'Model> * action: (GlobalTime -> IAnimationInstance<'Model> -> unit)

    /// Enqueues an animation in the slot with the given name.
    /// When the all previous instances in the slot have finished, the animation is computed, instantiated and the given action is performed.
    | Enqueue of name: Symbol * animation: ('Model -> IAnimation<'Model>) * action: (GlobalTime -> IAnimationInstance<'Model> -> unit)

    /// Performs the action for the current animation instance in the slot with the given name if it exists.
    | Perform of name: Symbol * action: (GlobalTime -> IAnimationInstance<'Model> -> unit)

    /// Removes the animation slot with the given name if it exists.
    | Remove  of name: Symbol

    /// Performs the action for the current animation instance of every slot.
    | Iterate of action: (GlobalTime -> IAnimationInstance<'Model> -> unit)

    /// Removes every animation slot for which the given predicate returns false.
    | Filter  of predicate: (AnimatorSlot<'Model> -> bool)