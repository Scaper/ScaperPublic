module TripConversion

open Observations
open StateSpace


///Given a list of Trips and the agent they were performed by, produce a DayPath of State/Decision tuples.
///If the trip list does not describe a valid daypath according to the model (e.g. does not end in an EndState) 
//then returns None.
let tripListToDayPath agent (trips:Trip seq) : DayPath option =
    let mutable currentState = startState agent

    let updateState d =
        let s = currentState
        currentState <- nextSingleState agent currentState d 
        (s, d)

    let travel (t:Trip) = 
        let l = match t.Activity with
                | Activity.Home -> Residence t.Destination
                | Activity.Work -> Workplace t.Destination
                | _ -> NonFixed (Some t.Destination)
        Travel(t.Mode, l)

    let path = [
        for trip in trips do

            let ts = timeFromTimeOnly trip.DepartureTime - 0.5

            while currentState.TimeOfDay < ts && currentState.TimeOfDay < DayEnd do
                yield Continue |> updateState
            
            if currentState.TimeOfDay < DayEnd then
                yield End |> updateState
                yield travel trip |> updateState
                yield Start trip.Activity |> updateState
        
        while currentState.TimeOfDay < DayEnd do
            yield Continue |> updateState 
        ]

    let endOK = match agent, currentState with
                | EndState -> true
                | _ -> false
    let pathOK = path |> List.forall (fun (s, _) -> match agent, s with 
                                                    | GoodState -> true 
                                                    | _ -> false )

    if pathOK && endOK then Some path else None
   
   
///Given a daypath and the agent that produced it, returns a trip list with the trips performed.
let dayPathToTripList (agent:Agent) (lc:int, daypath:DayPath) : Trip list =

    let rec makeTrips (path:DayPath) (tlist:Trip list) =
        match path with
        | [] | [_] -> tlist
        | (s, Travel(mode, dest)) :: (_, Start(activity)) :: tail -> 
            let trip = Trip(
                         indID = agent.IndID,
                         latentClass = lc,
                         activity = activity,
                         mode = mode,
                         origin = s.Location.Zone.Value,
                         destination = dest.Zone.Value,
                         departureTime = timeToTimeOnly s.TimeOfDay
                       )
            makeTrips tail (trip::tlist)
        | _ :: tail -> makeTrips tail tlist
    makeTrips daypath [] |> List.rev
    