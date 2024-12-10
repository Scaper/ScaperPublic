> [!IMPORTANT]
> You will need to program in F# to build models in Scaper. This document assumes you have some familiarity with F#.

State space specification
=============

This page is a guide to defining the state space of your model in this implementation of Scaper. The state space decscribes all possible states an agent can be in throughout the day and the possible transitions between them. There is also a guide to modifying the program to accommodate different [input data](InputData.md) and a guide to specifying the [utility function](Utility.md).

This page covers the two main modules relevant to state space specification: _Definitions_, which defines the core types and functions used in the model, and _StateSpace_, which defines states and transitions between them.

## Core model definitions

The _Definitions_ module covers the basic definitions necessary for the rest of the program to use. It is the first file loaded by F# in the _Model_ folder, meaning that it cannot use types or functions from any other modules in the folder. It is also marked with the `[<AutoOpen>]` attribute so it is automatically opened by all subsequently-loaded modules, including all others in the _Model_ folder.

### Time

As a dynamic model, Scaper represents time endogenously. The top of the _Definitions_ module defines four constants relevant to time which you can change as appropriate to your modelling context:
- `TimeResolutionMinutes` (currently 10) defines how many minutes comprise one integral timestep which the model uses to approximate the value function. It must divide evenly into 60 so that there are an integral number of timesteps in one hour. The size of the state space (and thus the program runtime) is inversely proportional to this value, so it represents a tradeoff between behavioural fidelity and computational complexity. 
- `DecisionStepMinutes` (currently 10) defines how many minutes a _continue_ decision moves an agent forward in time. This must be greater than or equal to `TimeResolutionMinutes`. Usual practice is to make the two equal. If this is changed, parameters should be re-estimated: while utility rate parameters are measured per minute so do not depend directly on this value, it determines how often agents can make choices and thus influences start-activity parameters.
- `DayStartHour` (currently 5am) and `DayEndHour` (currently 11pm) define the time window used by the model. The model could be made to simulate more than 24 hours by setting `DayEndHour` greater than 24. While these are defined as floats, the program has only been tested with integral values.

Changing these constant values should have the desired effect without requiring changes to other code; however, the program has not been extensively tested with values different than the current ones.

The bottom of the _Definitions_ module contains functions and values which use these four constants and which should generally not need to be changed. 

### Travel modes

Possible modes of travel are also defined in the _Definitions_ module using the `Mode` enum type:

```fsharp
type Mode = 
    | Car = 0
    | Transit = 1
    | Walk = 2
    | Bike = 3
```

To add a new mode, you should do the following:
- Add it to the `Mode` enum.
- Add level of service attributes (e.g. travel time, cost) for the new mode in the _Network_ module, as described in the guide on [input data](InputData.md#network-data).
- If applicable, update the variable `noCarModes` in the _StateSpace_ module to include the new mode. If the new mode has specific state transition rules, you will need to make further changes to the _StateSpace_ module (see [below](#state-space)).
- Update the utility function in the _Utility_ module (see [below](#utility-function)) as necessary to include the utility relevant to your mode. 
- Add any parameters specific to the new mode to your parameter CSV file.

The underlying int values you use to define modes should allow for numbers to be skipped (e.g. if you want to turn modes on and off by commenting, or if you have numbers representing specific modes in your data and want to be consistent). However, the program has only been tested with a contiguous set of underlying values.

The current model specification also keeps track of whether the agent has taken a car or bike with them from home, using the `Vehicle` enum type. The purpose of tracking this is to reflect agents' mode consistency across a day. `Vehicle` is a feature of the model specification, not a fundamental part of Scaper, and you may decide to take another approach, such as using latent classes for modal preferences or directly tracking which mode the agent used last.

### Activity purposes

Scaper defines activity purposes in the _Definitions_ module using the `Activity` enum type:

```fsharp
type Activity = 
    | Depart = 0
    | Arrive = 1
    | Home = 2
    | Work = 3
    | Shop = 4
    | Other = 5
```

The `Depart` and `Arrive` values are used internally by the program for computational efficiency; do not modify these.

The default model defines fairly few activity types. To add a new activity type, you should do the following:
- Add it to the `Activity` enum.
- If needed, modify the trip loading data (see [trip data guide](InputData.md#adjusting-for-your-trip-data)) to accommodate the new activity.
- Adjust the state space logic in the _StateSpace_ module (see [below](#state-space)) to account for the activity. For instance:
    - add it to the `MaxTrackedDuration` [map](#tracking-activity-duration);
    - if it is a discretionary activity which should be allowed in any zone, add it to the `discretionaryActivities` array;
    - if there is more complicated logic around when/where it should be available, modify the `options` [function](#agent-movement-through-the-state-space). 
- If needed, adjust the logic in the `tripListToDayPath` function in the _TripConversion_ module, which converts trips into day paths. Specifically, ensure that the activity maps to the correct `Location` in the local function `travel`.
- Update the utility function in the _Utility_ module (see [below](#utility-function)) as necessary to incorporate the utility for your new activity.
- Add parameters for your new activity type as necessary to your CSV parameters file.

As with the mode enum, it should be possible to define a non-contiguous set of underlying values for `Activity`, but this has not been tested.

### Locations

The final item of interest in the _Definitions_ module is the `Location` type, which as the name suggests represents a geographical location within the model.

```fsharp
type Location = 
    | Residence of zone:int
    | Workplace of zone:int
    | NonFixed of zoneOrAll:int option
```

The `Location` type is a [discriminated union](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions) which contains two linked pieces of information: what type of location it is (a residence, workplace, or non-fixed) and where it is located, i.e. the zone index. Residences and workplaces must have a specific zone, and other locations may be located in a specific zone (using `Some(value)`) or may point to all zones (using `None`). The type also defines the convenience properties `HasZone`, which returns true if the location points to a specific zone, and `Zone` which returns an int option containing the zone index if it exists.

The location is known during travel, meaning that your utility function can have specific utility parameters for travelling to or from the residence or workplace. You can link specific activity types to specific locations in the state space `options` function (see [below](#state-space)); for instance, the current implementation only allows the `Home` activity in the `Residence` location, the `Work` activity in the `Workplace` location, and `Shop` and `Other` activities in the `NonFixed` location. Your implementation may vary, for example by allowing agents to do the `Work` activity in other locations to allow for remote work. 

Note that the `NonFixed` location _does_ include the agent's home and work _zones_: the program makes a distinction between being in your residence or workplace and being in a non-fixed location in the same zone. It is not necessary to allow discretionary activities to be performed in the _Residence_ or _Workplace_ locations.

New activity types should generally be performed in one of the existing location types; for instance, a work-from-home activity would be performed at the `Residence` location. Most new out-of-home activities should be performed in the `NonFixed` location. A new location should only be added when the agent has a single fixed known location where the activity must be performed (e.g., school or child-dropoff/pickup). Adding a new location also necessitates adding a new agent attribute to store the agent's given zone index for the new location type, like `Agent.HomeZone` and `Agent.WorkZone` (see guide on [modifying observation data](InputData.md#observation-data)).

> [!IMPORTANT]
> For efficiency, code outside the _Model_ folder assumes that `NonFixed` is the only location that allows for travel to all zones. Any activity type which can be performed in any zone must be possible to start from a `NonFixed` location, and new location cases must only include single zones.


## States and decisions

The Scaper model can be seen as a Markov Decision Problem (MDP) which the agent moves through collecting rewards. The _StateSpace_ module defines the states of the MDP and the logic for how the agent may transition from state to state. It also defines an agent's start state and conditions for end states.

### States

A Scaper state is fundamentally a combination of activity purpose (`Activity`), location (`Location`) and time of day (`TimeOfDay`). The _StateSpace_ module defines a state as an immutable F# record. When an agent moves in the state space, new states are created instead of the existing ones being modified.

```fsharp
///A state in the state space
type State = {
    Activity : Activity
    Location : Location
    TimeOfDay : float
    Duration : int
    Vehicle : Vehicle
    HasWorked: bool
}
```

The current implementation adds three history variables to the `State` type: `Duration`, which tracks how long the agent has been doing the activity, `Vehicle`, which tracks whether the agent has a car or bike with them, and `HasWorked`, which tracks whether the agent has performed a work activity. How to add new history variables is described [below](#adding-history-variables).

In theory, a Scaper state represents an agent's specific presence in time and space. In the implementation, a `State` may represent an agent performing an activity in all possible zones at once, by having a `NonFixed` location with `None` as the zone option. This is an implementation choice for efficiency and its implications are noted below as appropriate.  

### Decisions

An agent's transition from state to state is represented by a `Decision` type, which is a [discriminated union](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions).

```fsharp
type Decision = 
    | Start of Activity
    | Continue
    | End
    | Travel of Mode * Location
```

There are four decision cases as seen above. The `Continue` and `End` decisions do not carry any additional information. The `Start` decision designates the activity purpose that will be started. The `Travel` decision contains both the mode of travel and the destination. The destination `Location` may be `NonFixed` with `None` as the zone option, in which case the decision represents travel to all possible zones.

In the [theoretical description](ScaperTheory.md) of Scaper there are really only two types of transitions: continuing the current activity and the joint choice of ending the current activity/travelling/starting a new activity. For efficiency reasons, the implementation makes this second joint choice into a sequence of three state transitions, each with its own `Decision`. Importantly, because of the way Scaper uses the future expected value of decisions, if propertly implemented this separation is mathematically equivalent to the theoretical description.


## Agent movement through the state space

The _StateSpace_ module defines two primary functions to describe how agents can move through the state space:
- `options` takes a `State` and returns all the cases of `Decision` possible to take in that state.
- `nextState` takes a `State` and a `Decision` and produces the state the agent will transition to.

### The `options` function

Focusing first on the `options` function: 

```fsharp
let options (explode:bool) (agent:Agent) (zones:int[]) (state:State) : Decision seq = seq {
    match state.Activity with
    | Activity.Depart ->
        // yield Travel options by mode
    | Activity.Arrive -> 
        // yield Start options matched with locations 
    | _ -> yield Continue
           yield End
```

The exact implementation of `options` will be determined by your model logic, but the broad structure shown above should be consistent across model specifications. In order to keep the implementation consistent with the theoretical description of Scaper, the type of `Decision` available is determined by the `Activity` of the current state:
- The `Depart` activity is followed by a `Travel` decision
- The `Arrive` activity is followed by a `Start` decision
- All other activity types (`Home`, `Work`, etc.) are followed by `Continue` and `End` decisions. 

Note that the options function does **not** need to restrict agent options based on whether they result in a feasible next state for the agent. For instance, the function does not check whether the agent has time available to travel to an activity and return home. These potential decisions can be generated by the `options` function; the resulting states will have a calculated value function of -∞ and thus zero probability. [This section](#defining-time-space-constraints) below discusses how to define time-space constraints on states.

On the other hand, constraints on the decision itself (as opposed to the resulting state) are usually easier to implement in the `options` function. For instance, the current implementation produces the set of modes available for `Travel` decisions with the code `if agent.OwnsCar then allModes else noCarModes`, which prevents agents who do not own cars from using cars. As this example demonstrates, you can use agent characteristics to differentiate the possibilities.

> [!NOTE]
> While the `options` function can be used to constrain agent behaviour as described above, you should also consider using the utility function to influence behaviour. This has the advantage of making unlikely behaviours, such as an agent without a car using the `Car` mode, merely unlikely in the model instead of impossible.

### The `nextState` function

Complementing the `options` function in describing how agents move from state to state is the `nextState` function, which generates the next State given a transition:

```fsharp
let private nextState (a:Agent) (s:State) (d:Decision) timeOfDay : State =
    match d with 
    | Start(activity) -> 
        // return State describing new activity 
    | End ->
        // return State with Depart activity
    | Continue -> 
        // return State with same activity
    | Travel(mode, dest) ->
        // return State with Arrive activity in new location
```

The basic structure of the `nextState` function is a pattern match on the type of `Decision`, as shown above. The function constructs new states using [record expressions](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/records): the old state is copied except for the properties listed.

Since a single `Decision` instance may represent travel to all possible zones, one `State`/`Decision` pair input to `nextState` may in fact represent states with different start times, reflecting the travel time differences. This problem is solved by providing a float input parameter `timeOfDay` to the `nextState` function which specifies the new state's time. The `nextState` function is not directly called by code outside `StateSpace`, but is instead called by one of two functions defined directly below:
- `nextSingleState` is used when simulating states with specific zones and ensures the `Location` of the provided decision has a single zone value.
- `nextIntegralTimeStates` is used when calculating the value function; it calls `nextState` multiple times with different start times to represent the different possible travel times. The implementation only considers future integral timesteps and approximates the expected value of the resulting state as an interpolation between the state's nearest integral timestep neighbours.

As a modeller, you shouldn't have to worry about these differences; just make sure that the new states you generate in `nextState` have `TimeOfDay = timeOfDay` and the implementation will do the work for you.


## Time-space constraints

One significant advantage of Scaper is its ability to endogenously represent time-space constraints. Using the `options` function and the `|EndState|BadState|GoodState|` active pattern described below, you as the modeller define what states should be considered acceptable for an agent to visit and what states it should be possible for an agent to end in. 

The value function code (defined outside the _Model_ folder, which you should not normally have to edit) ensures that unacceptable states are never reached by an agent. As the agent makes decisions on the basis of expected future utility, the model also ensures that states which are themselves acceptable but which cannot reach an acceptable state by the end of the day (e.g. being away from home too late) are also never reached.

### The `|EndState|BadState|GoodState|` active pattern

The `options` function discussed [above](#agent-movement-through-the-state-space) generates all potential decisions available to an agent without restricting these by whether they are feasible for the agent at the particular time. The implementation uses an [active pattern](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/active-patterns) function to determine the status of a state for a particular agent. The function is defined near the bottom of the _StateSpace_ module:

```fsharp
let (|EndState|BadState|GoodState|) (agent:Agent, state:State) = 
    match state.TimeOfDay with
    | t when t < DayStart || t > DayEnd -> BadState
    | t when t = DayEnd ->
        match state.Activity with
        | Activity.Home when state.Location = Location.Residence agent.HomeZone 
                          && state.HasWorked = agent.WorkZone.HasValue 
            -> EndState
        | _ -> BadState
    | _ ->
        match state.Activity with
        | Activity.Home when not state.Location.IsResidence -> BadState
        | Activity.Work when not agent.WorkZone.HasValue || not state.Location.IsWorkplace -> BadState
        | _ -> GoodState

```

The code above does the following:
- If the state's time of day is outside the time window, it is a `BadState`, which will result in the code immediately assigning it a value function of -∞.
- If the state is at the end of the day, it is an `EndState` if the activity is Home, the location is a Residence type with the agent's home zone, and the agent has worked only if they need to work; otherwise, it is a `BadState`. An `EndState` has a value function of zero.
- If the time of day is inside the time window, the function makes some checks for impossible state combinations (e.g. the home activity at a non-residential location) and otherwise declares the state a `GoodState`. These checks are probably redundant since the `options` and `nextState` functions only allow certain transitions.

You can define two main types of time-space constraints in this function. First, you can define end-state conditions under the `| t when t = DayEnd` matching case. You can use history variables such as `HasWorked` to ensure the agent performs some activity; these could be defined in such a way that the agent must perform the activity within a certain timeframe or for a certain minimum/exact/maximum duration. The [section below](#adding-history-variables) describes the considerations for adding new history variables.

Second, you can define within-day impossible states under the bottom matching case (`| _ ->`). For instance, if you wanted to make the `Shop` activity only available after 7am to reflect shop opening times, you could add the following case as part of the bottom `match state.Activity with` block:
```fsharp
| Activity.Shop when state.TimeOfDay < timeFromHourOfDay 7 -> BadState
```
It would also be possible to make this restriction in the `options` function by preventing the agent from starting a `Shop` activity before 7am; which you choose is a matter of preference.


### Tracking activity duration

The current state space implementation includes duration-dependent utility for activities. Since tracking duration as a state space variable explodes the size of the state space, each activity type has a maximum tracked duration to mitigate the increase. These maximums are defined in the `MaxTrackedDuration` variable in the _StateSpace_ module:

```fsharp
let private MaxTrackedDuration = 
    Map [ //numbers in hours
        Activity.Depart,  0.0
        Activity.Arrive,  0.0
        Activity.Home,    0.0
        Activity.Work,    12.0
        Activity.Shop,    0.0
        Activity.Other,   0.0
    ] |> Map.map (fun _ h -> iceil (h * DecisionStepsPerHour))
```

Numbers are given in hours, with fractions possible. Ihe code shown above that the `Work` activity is tracked for 12 hours and all other activities are not duration-tracked. The easiest way to turn duration-dependent modelling off completely is to set all the maximum values to 0.0.

If you add a new activity type, you will need to add it to this map with your desired tracking level.


### Adding history variables

To add a new history variable:
- Add it to the definition of `State`.
- Add the new history variable's logic to the `nextState` function to ensure the history variable changes when the agent makes the appropriate transitions.
- If the new variable should influence the possible decisions an agent can make in a state, modify the logic of the `options` function as appropriate.
- If the new variable must have a certain value by the end of the day, modify the logic of the `(|EndState|BadState|GoodState|)` active pattern as appropriate.
- If the new variable affects the agent's utility, modify the utility function in the _Utility_ module as appropriate.
- Modify the `CacheKeyState` type at the bottom of the _StateSpace_ module to allow the EV cache to properly differentiate states by history variable. In the code below, the last three types in the tuple definition (`int * Vehicle * bool`) are the three history variables, and they are passed into the `CacheKeyState` constructor on the next line:
  ```fsharp
  type CacheKeyState = Activity * int * int * Vehicle * bool
  let cacheKey (s:State) = CacheKeyState(s.Activity, s.Location.CaseTag, s.Duration, s.Vehicle, s.HasWorked)
  ```
