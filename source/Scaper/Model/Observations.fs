module Observations

open System
open FSharp.Data
open Parquet.Serialization.Attributes


///An individual who moves through the state space performing activities and travelling
type Agent(indID, age, female, homeZone, workZone, ownsCar, transitCard, income, hasKids, weight, workDuration) =

    member val IndID        : int           = indID         with get, set
    member val Age          : int           = age           with get, set
    member val Female       : bool          = female        with get, set
    member val HomeZone     : int           = homeZone      with get, set
    member val WorkZone     : Nullable<int> = workZone      with get, set
    member val OwnsCar      : bool          = ownsCar       with get, set
    member val TransitCard  : bool          = transitCard   with get, set
    member val Income       : int           = income        with get, set
    member val HasKids      : bool          = hasKids       with get, set
    member val Weight       : float         = weight        with get, set
    member val WorkDuration : Nullable<int> = workDuration  with get, set

    //empty constructor for serialization
    new() = Agent(0, 0, false, 0, Nullable(), false, false, 0, false, 0.0, Nullable()) 



///Movement between two activities in different locations. Used for data input and
///output, for example in writing simulated data out and for storing choicesets.
type Trip(indID, latentClass, activity, mode, origin, destination, departureTime) = 
    
    //Members (must be settable for deserialization)
    member val IndID         : int      = indID             with get, set
    member val LatentClass   : int      = latentClass       with get, set
    member val Activity      : Activity = activity          with get, set
    member val Mode          : Mode     = mode              with get, set
    member val Origin        : int      = origin            with get, set
    member val Destination   : int      = destination       with get, set
    member val DepartureTime : TimeOnly = departureTime     with get, set

    [<ParquetIgnore>]
    member x.TravelTime = 
                let depTS = timeFromTimeOnly x.DepartureTime
                Network.scalarTravelTime(x.Mode, x.Origin, x.Destination, depTS) +
                Network.scalarAccessTime(x.Mode, x.Origin, x.Destination, depTS) +
                Network.scalarWaitTime(x.Mode, x.Origin, x.Destination, depTS)
                |> TimeSpan.FromMinutes
    [<ParquetIgnore>]
    member x.ArrivalTime = x.DepartureTime.Add(x.TravelTime)
    
    override x.Equals (obj: obj): bool = 
        match obj with
        | :? Trip as y -> x.IndID = y.IndID && x.LatentClass = y.LatentClass && 
                          x.Activity = y.Activity && x.Mode = y.Mode && x.Origin = y.Origin && 
                          x.Destination = y.Destination && x.DepartureTime = y.DepartureTime
        | _ -> false

    override _.GetHashCode () : int = 
        hash (indID, latentClass, activity, mode, origin, destination, departureTime)

    //Default constructor for deserialization
    new() = Trip(0, 0, Activity.Depart, Mode.Car, 0, 0, TimeOnly())
    new(indID, activity, mode, origin, destination, departureTime) =  Trip(indID, 0, activity, mode, origin, destination, departureTime)

///An agent and their observed trips
type Observation = Agent * (Trip list)


type AgentCsv = CsvProvider<DataSources.agentFile, Schema="work_zone=int?">
type TripCsv = CsvProvider<DataSources.tripFile>

///If N is Some int, use Seq.take to take the first N items of the sequence. 
///If N is None, return the whole sequence.
let inline truncateIfSome (N:int option) (items:'T seq) =
    match N with
    | Some n -> Seq.truncate n items
    | None -> items

let rndWz = new System.Random()

let private randomWorkzone (age:int, income:decimal) : Nullable<int> =
    if income > 0 && age > 16 then rndWz.Next 90 else System.Nullable() 

///Converts a row in the agent csv file to an Agent
//if r.Work_zone != null then LandUse.zoneIndex[r.Work_zone] else
let private agentFromRow (r : AgentCsv.Row) : Agent = 
    Agent(
        indID = r.Id,
        age = r.Age,
        female = (r.Sex = "f"),
        homeZone = LandUse.zoneIndex[r.Home_zone],
        workZone =  randomWorkzone (r.Age, r.Income),
        income = int (0.0001 * float r.Income),
        ownsCar = (r.NumberOfCars > 0),
        transitCard = false,
        hasKids =  r.SmallChildren,
        weight = float r.Weight,
        workDuration = Nullable()
    )



///Gets the agents in agentFile as a list. Returns the first takeMax agents,
///or all agents if takeMax is None.
let loadAgents (takeMax:int option) : Agent seq =  
    AgentCsv.Load(DataSources.agentFile).Rows 
    |> Seq.map agentFromRow 
    //|> Seq.where (fun a -> a.WorkZone.IsSome)
    |> truncateIfSome takeMax


///Converts a row in the trip csv file to a Trip
let private tripFromRow (r : TripCsv.Row) : Trip = 
    Trip(
        indID = r.IndID,
        activity = Activity.Parse(r.Activity),
        mode = Mode.Parse(r.Mode),
        origin = r.Origin,
        destination = r.Destination,
        departureTime = TimeOnly.FromTimeSpan(r.DepartureTime)
    )


///Loads a map of where the values are Trip lists grouped by agent and the
///keys are the agent's ID.
let private loadTripMap (takeMax:int option) = 
    TripCsv.Load(DataSources.tripFile).Rows
    |> Seq.map tripFromRow
    |> Seq.groupBy (fun trip -> trip.IndID)
    |> truncateIfSome takeMax
    |> Seq.map (fun (id, trips) -> 
        id, trips |> Seq.sortBy (fun trip -> trip.DepartureTime)
                  |> List.ofSeq)
    |> Map.ofSeq


///Loads the first takeMax observations, or all observations if takeMax is None.
///If the trips file does not contain trips for a given agent, it is still included with
///an empty trips list (i.e. it is assumed that the agent stayed home all day).
let loadObservations (takeMax:int option) : Observation seq =
    let tripMap = loadTripMap None
    loadAgents takeMax 
    |> Seq.map (fun agent -> 
        agent, defaultArg (Map.tryFind agent.IndID tripMap) List.Empty)    