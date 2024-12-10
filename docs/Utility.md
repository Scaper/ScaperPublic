> [!IMPORTANT]
> You will need to program in F# to build models in Scaper. This document assumes you have some familiarity with F#.

Specifying the utility function
==========

This page is a guide to specifying the utility function of a Scaper model in this F# implementation. There is also a guide to modifying the program to accommodate different [input data](InputData.md) and a guide to defining the [state space](StateSpace.md).

As discussed in the guide to [Scaper theory](ScaperTheory.md), agents in Scaper make decisions based on the sum of the immediate utility of a state space transition and the expected future utility of the resulting state. The implementation makes these calculations in code outside the _Model_ folder, so as a modeller using Scaper, you don't need to worry about the expected future utility of a state. You are responsible for defining the immediate utility (also called reward) of performing an action for an agent. You do this in the _Utility_ module. The rest of this page will refer to the immediate utility simply as utility.

The implementation makes a few assumptions about the utility function you should be aware of:
- The utility function is linear in parameters. This is assumed by the estimation code for efficient utility calculations. This implementation does not support logsum size parameters with several scaled components.
- Travel utility, activity-start utility and activity-continue utility are additively separable. Travel utility does not depend on the specific activity purpose the agent starts. This is an implementation choice for efficient computation of the all-zones to all-zones travel utilities.

These assumptions are built in to the way the code calculates the utility function in the `classVariables` and `decisionVariables` functions, and also by code outside the _Model_ folder. Going beyond these assumptions would require much deeper code changes than covered in the guides.

## How the implementation calculates utility

Like most discrete choice models, an agent's (immediate) utility for a state-space transition is the sum of the products of parameter values and their associated variable values. This is sometimes expressed as a dot product of a parameter vector and a variable/attribute vector; this is the way the estimation code represents utility.

In simulation (and choiceset generation), the program maintains a list of parameters with names and values. You are responsible for defining a function which produces a sequence of variable values with the parameter names they should be multiplied by. The program takes care of finding the correct parameter value and performing the multiplication.

### Parameters

The `Parameter` type is defined in the _Parameters_ module in the _Estimation_ folder, as it is used by the estimation code as well as the core model code. Of relevance to you as a modeller is that the parameter has a string `Name` and float `Value` (the `Estimate` and `EstIndex` properties are used in estimation):

```fsharp
type Parameter = {
    Name : string
    Value : float
    Estimate : bool
    EstIndex : int
}
```

The `Parameter` type maps to the parameter CSV file you specifify in the `ParameterInputFile` literal at the top of the _Utility_ module. If you have a parameter called `p` the CSV file looks like this:

| parameter     | value         | estimate      |
| ------------- | ------------- | ------------- | 
| nClasses      | 1             | FALSE         |
| p.Name        | p.Value       | p.Estimate    |

The first row of the parameter CSV file should define the number of classes using the parameter name _nClasses_. If you omit this it will default to 1, i.e. a non-latent class model.

### Variables

The _Parameters_ module also defines the _Variable_ type, which is a tuple comprising the parameter name (string) and a sequence of values that should all be multiplied by this parameter value then added to the sum.

```fsharp
type Variable = string * Mat seq
```

The utility functions you define, which are discussed in detail in the below sections, return sequences of the `Variable` type comprising all the variables that should be summed for the given situation.

The `Variable` type contains values of type `Mat`, which is a custom implementation of a matrix with efficient scaling and broadcasting functionality. This is because the implementation uses one `Decision` to cover travel from and to more than one zone, with different travel times/costs and thus different utilities. As a modeller, you generally do not need to worry about the underlying matrix implementation; for instance, in calculating the utility of a travel decision, you use the `World` instance passed into the utility function to produce the appropriate variable values.

It is also worth noting that the `Variable` value is stored as a sequence of `Mat` instances rather than a single value. This is done to allow for efficient interpolation between peak and off-peak travel periods. Instead of interpolating between peak and non-peak first then passing the resulting `Mat` to the utility function, the program notes how the peak and non-peak LOS matrices should be scaled and passes them both to the utility function for scaling by the parameter value and summation, avoiding a few potentially expensive calculations. Again, as a modeller you don't need to worry about this unless you are making substantial changes outside the _Model_ folder.


## Class membership utility

This implementation of Scaper defines a latent class Scaper model, which requires a class membership utility function. The _Utility_ module defines the function `classVariables` for this purpose.


### Non-latent class models

Defining the class membership function for a non-latent class version of Scaper is easy; just return the empty sequence from the `classVariables` function:

```fsharp
let classVariables (agent:Agent) (c:int) : Variable seq = Seq.empty
```

### Latent class models

The class membership function for a specification of Scaper with _N_ latent classes should use the agent `agent` and latent class number `c` between 0 and N-1 to produce a sequence of type `Variable`:

```fsharp
let classVariables (agent:Agent) (c:int) : Variable seq = seq {
    if c > 0 then
        $"Class{c}_Constant", one
        //other variables
}
```

Generally, class 0 serves as the reference class and does not have any variables associated with it; thus the sequence only returns anything when `c > 0`.

The line `$"Class{c}_Constant", one` produces a tuple of type `string * Mat seq` as required for the `Variable` type. The interpolated string produces the parameter name (_Class1\_Constant, Class2\_Constant, etc._). The special value `one`, indicating a scalar value of 1 to multiply by the appropriate parameter value, is defined at the top of the _Utility_ module. 

Within the `classVariables` function, you can use conditional statements on the agent's properties to include socioeconomic or household attributes in the class membership utility. For instance, the current implementation includes a variable for agents under age 35:

```fsharp
if agent.Age < 35 then
    $"Class{c}_Under35", one
```

If you want to include agent characteristics not currently implemented as properties of the `Agent` type, follow the guide to [modifying agent attributes](InputData.md#modifying-agent-attributes).



## State-space transition utility

The _Utility_ module defines the core utility function, called `decisionVariables`, which is used to calculate an agent's reward for all state transitions. The overall structure of the `decisionVariables` function is:

```fsharp
let decisionVariables (agent:Agent) (world:World) (c:int) (state:State, decision:Decision) : Variable seq = 
    match decision with
    | Travel(mode, dest) -> seq { ... } 
    | Start(activity) -> seq { ... }
    | Continue -> seq { ... } 
    | End -> Seq.empty 
```

The function accepts as input the `Agent` performing the transition and the possibly-sampled `World` in which the transition is happening, as well as the latent class index `c`. Most importantly, it requires the current `State` and the `Decision` that has been made.

As noted above, the utility is separated into three cases of `Decision`: `Travel`, `Start`, and `Continue`. In the current implementation, the `End` activity does not have an associated utility and so returns the empty sequence.  The F# pattern matching gives you access to the components of the travel decision (`mode` and `dest`) and the start decision (`activity`) as appropriate. The utility function uses these in combination with the current state and the agent to produce all the utility variables. 

Note that the travel utility knows the destination `Location` but not what activity will be started next, as the travel and start activities are made sequential in implementation for efficiency. (It is worth noting again that due to the structure of Scaper this does not fundamentally change the way the model works, just limits the way in which the utility function can be specified.) If you want to link mode choice with activity purpose in your utility function, you can do this in the `Start` branch of the utility. You will have to include a [state](StateSpace.md#state) history variable which stores the mode used (or a proxy, such as the `Vehicle` state variable). This history variable is then available to the utility function when the agent is choosing what activity to start. As the agent makes decisions on expected future utility, including this utility in the start-activity decision also influences the agent's prior travel decision.

The current implementation defines the entire utility function within the `decisionVariables` function, with a helper interpolation function. However, you could choose to define utility functions for each type of `Decision` separately, then call these functions in the appropriate cases. 

The subsections below will discuss each section of the utility function, with reference to the currently implemented utility function for reference.

### Travel utility

The travel part of the utility function produces a `Variable` sequence representing the utility of the agent travelling from the current state to the given location `dest` by the given `mode`:

```fsharp
match decision with
| Travel(mode, dest) -> 
    let odInfo = mode, state.Location, dest, state.TimeOfDay
    seq { ... }
| ...
```

For convenience, the mode, current location, destination location and time of day are stored in the tuple `odInfo` (short for origin-destination information). This tuple is constructed in the order in which the `World` network LOS functions expect the information, and will be passed into these functions as shown below.

The variables produced by the currently-implemented travel utility include a trip constant, travel time, transit wait and access time, travel cost and a zone correction term. These are shown and briefly discussed here. Note that all parameters include the suffix `_Class{c}` in the interpolated string parameter name, which adds the latent class index so the program uses the appropriate class parameter.

The mode-specific trip constant uses string interpolation to include the name of the `Mode` enum `mode` to produce the parameter name. It outputs the special scalar constant value `one`, which will be multiplied by the appropriate parameter value and added to the utility:

```fsharp
$"{mode}_Trip_Class{c}", one    
```

The mode-specific travel time variable demonstrates the use of the utility function's `world` instance to obtain variable values. In this case, the function `world.TravelTime` is called with parameter `odInfo` to get the appropriate `Mat seq` for multiplication by the travel time parameter value:

```fsharp
$"{mode}_TravelTime_Class{c}", world.TravelTime odInfo
```

Some variables should be added only when certain conditions are met. In the current specification, variables for wait time and walk access time are added only for the transit mode. Wait time represents time spent at transit stops or stations waiting for a bus/train. Access time represents the time necessary to walk to get to and from the stop or station, and thus is scaled by the walk travel time parameter. Like the travel time variable, the `world` instance is used to get the relevant values:

```fsharp
if mode = Mode.Transit then
    $"Transit_WaitTime_Class{c}", world.TravelWait odInfo
    $"Walk_TravelTime_Class{c}", world.TravelAccess odInfo
```

The travel cost parameter is also conditional since agents who have monthly transit cards are considered to pay for the cards with their work travel only, and so any non-work transit trips for these agents should not include a travel cost. In the current specification, the travel cost is divided by the agent's income using the `scale` function from the `Matrix` module. That function can be used if the raw values provided by the `World` LOS functions is not sufficient for your utility function.

```fsharp
if not(mode = Mode.Transit && agent.TransitCard && not(state.Location.IsWorkplace || dest.IsWorkplace)) then
    $"TravelCost", scale (world.TravelCost odInfo) (1.0/float agent.Income)
```

The final element of the travel utility is the zone correction term. This should not be removed, and probably should not be changed! It ensures that if zone sampling is used (`world.IsSampled = true`) then travel to non-fixed destinations includes a correction term which compensates for the reduced number of zones the agent can travel to:

```fsharp
if dest.IsNonFixed && world.IsSampled then
    ZoneCorrectionParam, world.Corrections (state.Location, dest)
```

### Start-activity utility

The start-activity part of the utility function produces a `Variable` sequence representing the utility of the agent starting a new activity.

```fsharp
match decision with
| ...
| Start(activity) -> 
    seq { ... }
| ...
```

The variables produced by the start-activity include an activity-specific constant, zone-specific attraction variables and a time-of-day dependent work start term.

The activity-specific constant is included for any activity in `StateSpace.discretionaryActivities`, currently `Shop` and `Other`: 

```fsharp
if Array.contains activity StateSpace.discretionaryActivities then
    $"{activity}_Start_Class{c}", one
```

The next part of the start-activity utility uses a pattern match on the activity purpose to introduce zone attraction variables. For the `Shop` activity it uses the `world.LogEmp` function to produce a log employment variable; for the `Other` activity it uses the `world.LogPop` function to produce a log population variable. Both of these functions take a `Location` parameter which is given by `state.Location`. These are the only two land use variables in the current code but this demonstrates how these zonal attributes can be used in the utility function.  

```fsharp
match activity with 
| Activity.Shop -> 
    $"Shop_LogEmp_Class{c}", world.LogEmp state.Location
| Activity.Other -> 
    $"Other_LogPop_Class{c}", world.LogPop state.Location
| ...
```

As part of the same pattern match on activity purpose, the `Work` activity is matched to produce linearly-interpolated time-dependent start parameters:

```fsharp
match activity with 
| ...
| Activity.Work -> 
    yield! interpolate $"Work_Start_Class{c}" 5 3 20 1.0 (timeToHourOfDay state.TimeOfDay)
| ...
```

The `interpolate` function is discussed in the subsection below. The `yield!` keyword is used to merge the sequence produced by the function into the main return sequence. The `interpolate` function is passed in the time of the current state as an hour.


### Interpolation of time-dependent variables

The example just above demonstrated the use of the `interpolate` function for work start time. The function is define in the _Utility_ module for use in the utility function, and its signature is:

```fsharp
let interpolate (varBaseName:string) (bottom:int) (delta:int) (top:int) (scale:float) (value:float) : Variable seq
```

The `interpolate` function sets up a piecewise linear function defined by parameter values from `bottom` to `top` inclusive, separated by `delta`. In the example above, let's assume we're in latent class `c=0`. The function is called as `interpolate "Work_Start_Class0" 5 3 20 1.0 (timeToHourOfDay state.TimeOfDay)`, so the piecewise function is defined at points {5, 8, 11, 14, 17, 20} corresponding to hours of a day.

The value to be interpolated in this function is `value`. Using the same example from above, let's say the time of day is 9:00am, so `value = 9`. Since 9 is one-third of the way between 8 and 11, the linear interpolation should weight the 8 parameter by 2/3 and the 11 parameter by 1/3. The function will produce two variables with `varBaseName` and the 
- Work_Start_Class0_8, scalar 2/3
- Work_Start_Class0_11, scalar 1/3

The variables returned by the `interpolate` function get passed on to the utility function to be matched with parameters by name. Let's say that the parameters have the following values defined in the parameters CSV file:

| parameter               | value         |
| ----------------------- | ------------- |
| Work_Start_Class0_8     | 1.5           |
| Work_Start_Class0_11    | 0.3           |

The final interpolated utility in our example is (1.5 × 2/3) + (0.3 × 1/3) = 1.1.

The `scale` parameter of the `interpolate` function scales the entire resulting utility, so if `scale = 5` then the utility returned in our example would be 5.5 instead of 1.1. This is used by the continue-activity utility to scale the per-minute utility rates to cover the full timestep, as discussed in the next subsection.

If the `interpolate` function as currently implemented does not need your modelling needs, you can modify it as appropriate. For instance, you might want to specify the estimated parameters at arbitrarily defined points rather than regularly-spaced ones, in which case you would pass in an array of these points to replace `bottom`, `delta` and `top`. The `interpolate` function demonstrates how you can work within the structure of the linear-in-parameters utility function to get a bit creative with the utility function.

### Continue-activity utility

The continue-activity part of the utility function produces a `Variable` sequence representing the utility of the agent continuing the current activity.

```fsharp
match decision with
| ...
| Continue -> 
    let ds = decisionStepMinutes state.TimeOfDay
    seq { ... }
```

The length of the decision step in minutes, i.e. the difference between the current and next state time, is stored in `ds` for convenience. Defined in the _StateSpace_ module, the function `decisionStepMinutes` usually returns `Definitions.DecisionStepMinutes` except at the end of the day when it is the difference between `Definitions.DayEnd` and the current state time.

The main part of the continue-activity utility is a pattern match on the current activity. The `Home` activity uses the `interpolate` function to produce a linearly interpolated function of time of day, similar to the work start variables above.

```fsharp
match state.Activity with
| Activity.Home -> 
    yield! interpolate $"Home_Continue_Class{c}" 5 3 23 ds (timeToHourOfDay state.TimeOfDay)
| ...
```

Note in the above that the the length of the decision step `ds` is used to scale the home continue utilities up by the size of the timestep. This means that the utility rate parameters are expressed per minute and ensures that the utility at the end of the day is dealt with consistently to the rest of the day.

The `Work` activity also uses the `interpolate` function, but with elapsed activity duration instead of time of day. It uses a maximum of 12 hours, consistent with its [maximum tracked duration](StateSpace.md#tracking-activity-duration). It also uses the decision step `ds` to scale the resulting utility so the utility rate parameters are expressed per minute.

```fsharp
match state.Activity with
| ...
| Activity.Work ->
    yield! interpolate $"Work_Continue_Class{c}" 0 3 12 ds (timeToHours state.Duration)
| ...
```

Activities other than home and work (currently `Shop` and `Other`) get a constant utility rate parameter as they are not duration-tracked in the current implementation. You may decide instead that some or all discretionary activities should have duration-dependent utilities, in which case you would alter this line to be similar to the work continue utility above. The continue-activity utility also uses `ds` as a scalar (instead of `one`) for the same reason as above: so the parameter expresses a per-minute utility rate comparable to the other activity types, and treats end-of-day short decisions consistently with the rest of the day.

```fsharp
match state.Activity with
| ...
| _ ->
    $"{state.Activity}_Continue_Class{c}", scalar ds
```

Finally, the model includes a parking cost variable which matches with the _TravelCost_ parameter. Parking cost is provided by the `world.ParkingRate` function, which provides a per-minute parking rate in the same currency as travel cost. It is scaled by `ds` to make the cost reflect the entire timestep; like travel cost it is also scaled by the reciprocal of the agent's income. Parking cost is included only for non-residence locations when the agent has a car with them.

```fsharp     
if not(state.Location.IsResidence) && state.Vehicle = Vehicle.Car then
    $"TravelCost", scale (world.ParkingRate state.Location) (ds/float agent.Income)
```
