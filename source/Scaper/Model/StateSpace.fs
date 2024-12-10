module StateSpace

open World
open Observations


///A state in the state space
type State = {
    Activity : Activity
    Location : Location
    TimeOfDay : float
    Duration : int
    Vehicle : Vehicle
    HasWorked: bool
}

///A transition between states
type Decision = 
    | Start of Activity
    | Continue
    | End
    | Travel of Mode * Location


///A path through the state space
type DayPath = (State*Decision) list


///An array of all modes
let private allModes = enumValues<Mode>()

let private noCarModes = allModes //[| Mode.Transit; Mode.Walk; Mode.Bike |]

///Activities that can be performed in any zone
let discretionaryActivities = [| Activity.Shop; Activity.Other |]


///The maximum duration that we will make distinctions between by type of activity.
///Given in hours and converted to decision steps.
let private MaxTrackedDuration = 
    Map [ //numbers in hours
        Activity.Depart,  0.0
        Activity.Arrive,  0.0
        Activity.Home,    0.0
        Activity.Work,    12.0
        Activity.Shop,    0.0
        Activity.Other,   0.0
    ] |> Map.map (fun _ h -> iceil (h * DecisionStepsPerHour))

///The max duration for an activity
let maxDuration (agent:Agent) (act:Activity) = 
    if act = Activity.Work && agent.WorkDuration.HasValue then
        max MaxTrackedDuration[act] agent.WorkDuration.Value
    else 
        MaxTrackedDuration[act]

///How much time (in our time resolution) does a 'continue' decision take?
let inline decisionStep (time:float) =
    min DecisionStep (DayEnd - time)

///How many minutes does a 'continue' decision take?
let inline decisionStepMinutes (time:float) =
    (decisionStep time) * TimeResolutionMinutes


///Produces a list of all possible decisions an agent could make in a given state.
///The explode parameter governs whether discretionary activities are given one 
///decision for each zone (useful for simulation to choose between these decisions)
///or one decision across all zones (better for doing EV calculations).
let options (explode:bool) (agent:Agent) (zones:int[]) (state:State) : Decision seq = seq {
    
    match state.Activity with
    
    | Activity.Depart -> 
        for mode in (if agent.OwnsCar then allModes else noCarModes) do

            if not(state.Location.IsResidence) then
                yield Travel(mode, Location.Residence agent.HomeZone)
            
            if agent.WorkZone.HasValue && not(state.Location.IsWorkplace) then
                yield Travel(mode, Location.Workplace agent.WorkZone.Value)

            if explode then
                for z in zones -> Travel(mode, Location.NonFixed (Some z))
            else
                yield Travel(mode, Location.NonFixed None)

    | Activity.Arrive -> 
        match state.Location with
        | Residence _ -> yield Start(Activity.Home)
        | Workplace _ -> yield Start(Activity.Work)
        | NonFixed _ -> yield! Seq.map Start discretionaryActivities

    | _ -> yield Continue
           yield End
}


///Internal function to produce the next state from the given decision at the given time of day.
///Used by the two functions below which get used in different contexts.
let private nextState (a:Agent) (s:State) (d:Decision) timeOfDay : State =
    
    match d with 
    | Start(activity) -> 
        { s with TimeOfDay = timeOfDay
                 Activity = activity
                 Duration = min 1 (maxDuration a activity)
                 HasWorked = s.HasWorked || (not(a.WorkDuration.HasValue) && activity = Activity.Work) 
        }
    | End ->
        { s with TimeOfDay = timeOfDay
                 Activity = Activity.Depart
                 Duration = 0 
        }
    | Continue -> 
        { s with TimeOfDay = timeOfDay
                 Duration = min (s.Duration + 1) (maxDuration a s.Activity)
                 HasWorked = 
                    if a.WorkDuration.HasValue 
                        then (s.HasWorked || (s.Activity = Activity.Work && s.Duration + 1 = a.WorkDuration.Value)) && not (s.Activity = Activity.Work && s.Duration = a.WorkDuration.Value)
                        else s.HasWorked 
        }
    | Travel(mode, dest) ->
        { s with TimeOfDay = timeOfDay
                 Activity = Activity.Arrive
                 Location = dest
                 Duration = 0
                 Vehicle = 
                    if dest.IsResidence then Vehicle.NoVehicle
                    else if s.Location.IsResidence then vehicleOf mode
                    else s.Vehicle
        }


///Gets the state that follows the decision. Must represent a state with a known location
///(i.e. one generated as part of a simulated day). Not used in the value function - that 
///uses nextIntegralTimeStates instead and interpolates the resulting EVs
let nextSingleState (a:Agent) (s:State) (decision:Decision) : State =  
    let dTime = 
        match decision with 
        | End -> 0.0
        | Travel(mode, dest) ->
            let modt = mode, s.Location.Zone.Value, dest.Zone.Value, s.TimeOfDay
            (Network.scalarTravelTime modt + Network.scalarWaitTime modt + Network.scalarAccessTime modt) |> timeFromMinutes
        | _ -> decisionStep s.TimeOfDay
    
    nextState a s decision (s.TimeOfDay + dTime)


///Gets the states that follow the decision with integer timesteps. Used in the value function 
///to ensure only integral timestep states are considered; other states' EVs are linear 
///interpolations of these states.
let nextIntegralTimeStates (a:Agent) (w:World) (s:State) (d:Decision) : State list = [
    let dTimes = match d with 
                 | Travel(mode, dest) -> w.TravelTimesteps(mode, s.Location, dest)
                 | _ -> let f = (frac s.TimeOfDay) + (decisionStep s.TimeOfDay)
                        [| ifloor f .. iceil f |]
    for dTime in dTimes ->
        nextState a s d (floor s.TimeOfDay + float dTime)
]

///The start state for a given agent.
let startState (agent:Agent) = {
    Activity = Activity.Home
    Location = Location.Residence agent.HomeZone
    TimeOfDay = 0.0
    Duration = 0
    Vehicle = Vehicle.NoVehicle
    HasWorked = false
}

///An active pattern for whether the given state is an end state, 
///a bad state, or a good state for the given agent.
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



///A tuple of all state variables EXCEPT TimeOfDay and the zone within Location. Used for caching state expected utility values.
///Must be updated if the definition of a State changes.
type CacheKeyState = Activity * int * int * Vehicle * bool
let cacheKey (s:State) = CacheKeyState(s.Activity, s.Location.CaseTag, s.Duration, s.Vehicle, s.HasWorked)
