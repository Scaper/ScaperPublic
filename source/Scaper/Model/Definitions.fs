[<AutoOpen>]
module Definitions

open System

// *** TIME *** //

///How many minutes between distinct (integral) time states? Must evenly 
///divide into 60, i.e. provide an integral number of time states per hour.
[<Literal>]
let TimeResolutionMinutes = 10.0

///How many minutes should a 'continue' decision count for? Must be >= TimeResolutionMinutes
[<Literal>]
let DecisionStepMinutes = 10.0

///The hour of day when agents will start their activities
[<Literal>]
let DayStartHour = 5.0  //5am

///The hour of day when agents will end their activities
[<Literal>] 
let DayEndHour = 23.0   //11pm

// *** ENUMS *** //

///A mode of travel between activities
type Mode = 
    | Car = 0
    | Transit = 1
    | Walk = 2
    | Bike = 3

///A vehicle that a person may have with them
type Vehicle = 
    | NoVehicle = 0
    | Car = 1
    | Bike = 2

let vehicleOf (m:Mode) : Vehicle =
    match m with
    | Mode.Car -> Vehicle.Car
    | Mode.Bike -> Vehicle.Bike
    | _ -> Vehicle.NoVehicle


///The activity being performed in a state
type Activity = 
    | Depart = 0
    | Arrive = 1
    | Home = 2
    | Work = 3
    | Shop = 4
    | Other = 5

///The location of an agent performing an activity
[<Struct>]
type Location = 
    | Residence of zone:int
    | Workplace of zone:int
    | NonFixed of zoneOrAll:int option

    ///True if this Location has a zone
    member l.HasZone with get() = match l with | NonFixed None -> false | _ -> true

    ///The zone this Location points to (or None)
    member l.Zone with get() = match l with | Residence z | Workplace z -> Some z | NonFixed zopt -> zopt

    ///An int representing which case this location belongs to
    member l.CaseTag = match l with | Residence _ -> 0 | Workplace _ -> 1 | NonFixed _ -> 2

// *** TIME FUNCTIONS *** //

//should generally not change the values below - they will be
//calculated based on the four above

if TimeResolutionMinutes > DecisionStepMinutes then
    raise(Exception("Decision step size must be equal or greater than timestep size!"))

///How many timesteps are in a decision step
[<Literal>]
let DecisionStep = DecisionStepMinutes/TimeResolutionMinutes

[<Literal>]
let TimeResolutionPerMinute = 1.0 / TimeResolutionMinutes
[<Literal>]
let TimeResolutionPerHour = 60.0 / TimeResolutionMinutes

[<Literal>]
let DecisionStepsPerHour = 60.0 / DecisionStepMinutes

[<Literal>]
let DayStart = 0.0
[<Literal>]
let DayEnd = (DayEndHour-DayStartHour)*TimeResolutionPerHour

///The number of timesteps per day, inclusive of DayStart and DayEnd
let DayLength = int (DayEnd - DayStart) + 1

let inline timeToMinutes (ts:float) = ts * TimeResolutionMinutes
let inline timeFromMinutes m : float = m / TimeResolutionMinutes

let inline timeToHours (ts:float) = ts / TimeResolutionPerHour
let inline timeFromHours h : float = h * TimeResolutionPerHour
let inline timeToHourOfDay (ts:float) = timeToHours ts + DayStartHour
let inline timeFromHourOfDay h : float = timeFromHours (h - DayStartHour)

let inline timeFromTimeOnly (t:TimeOnly) : float = 
    (t.ToTimeSpan() - TimeSpan.FromHours(DayStartHour)).TotalMinutes |> timeFromMinutes
let inline timeToTimeOnly (ts:float) : TimeOnly = 
    (timeToMinutes ts |> TimeSpan.FromMinutes) + TimeSpan.FromHours(DayStartHour) |> TimeOnly.FromTimeSpan


let inline durationToHours (ds:float) = ds / DecisionStepsPerHour



///The name of the zone correction parameter - used in the code but not in the parameter files
[<Literal>]
let ZoneCorrectionParam = "ZoneCorrection"