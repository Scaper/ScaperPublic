[<RequireQualifiedAccess>]
module Utility

open Parameters
open Observations
open StateSpace
open World
open Matrix


///The file with parameters for the utility function. Will be searched for in estimates base folder; may reside in a subfolder.
[<Literal>]
let ParameterInputFile = "startparams.csv"

///The file to load choiceset for in estimation. Will be searched for in choiceset base folder; may reside in a subfolder.
[<Literal>]
let ChoicesetInputFile = "yourinputfile.parquet"

/// The parameter file for the zone sampling MNL utility function
[<Literal>]
let ZoneSamplingParameterFile = "zonesampling.csv"


///Represents the value 1.0 to use as a dummy
let private one : Mat seq = scalar 1.0


///The utility function for latent class assignment. Produces a sequence of variables 
///(string/float[,] tuples) describing the utility of belonging to the given class for 
///the given agent. The Mats provided will each be multiplied by the value of the named 
///parameter, then the sequence will be summed to produce the class utility.
let classVariables (agent:Agent) (c:int) : Variable seq = seq {
        
    if c > 0 then
        $"Class{c}_Constant",                                               one
        if agent.Income >= 32 then
            $"Class{c}_HighIncome",                                         one
        if agent.Age < 35 then
            $"Class{c}_Under35",                                            one
        if agent.Age > 60 then
            $"Class{c}_Over60",                                             one
        if agent.Female then 
            $"Class{c}_Female",                                             one
        if agent.HasKids then 
            $"Class{c}_HasKids",                                            one
        if agent.OwnsCar then
            $"Class{c}_HasCar",                                             one
}

///Creates one or two Mats with values interpolated, for use in time-of-day or
///duration based variables. The variable name(s) will be varBaseName_# where # is the
///lower and upper bound of the bin. If the value matches a bin edge exactly, only that
///variable will be returned. Bins are constructed starting at start with width delta.

let interpolate (varBaseName:string) (bottom:int) (delta:int) (top:int) (scale:float) (value:float) : Variable seq =  
    let idx = if value <= bottom then 0.0
              else if value >= top then float ((top - bottom)/delta)
              else (value - float bottom)/(float delta)
    let f = frac idx
    let low = (ifloor idx)*delta + bottom             //lower variable label
    seq {
        $"{varBaseName}_{low}", scalar (scale*(1.0 - f))
        if f > 0 then $"{varBaseName}_{low + delta}", scalar (scale*f)
    }


///The one stage utility function. Produces a sequence of variables (string/float[,] tuples) 
///describing the utility of the given decision from the given state, in the context of 
///the given agent belonging to latent class c. The 2d arrays produced will each be multiplied 
///by the value of the named parameter, then the sequence will be summed to produce the 
///immediate utility.

let decisionVariables (agent:Agent) (world:World) (c:int) (state:State, decision:Decision) : Variable seq = 
    
    match decision with
    | Travel(mode, dest) -> 
        let odInfo = mode, state.Location, dest, state.TimeOfDay
        seq {

            $"{mode}_Trip_Class{c}",                                                    one
        
            $"{mode}_TravelTime_Class{c}",                                              world.TravelTime odInfo            
                  
            if mode = Mode.Transit then
                $"Transit_WaitTime_Class{c}",                                           world.TravelWait odInfo
                $"Walk_TravelTime_Class{c}",                                            world.TravelAccess odInfo
            
            if not(mode = Mode.Transit && agent.TransitCard && not(state.Location.IsWorkplace || dest.IsWorkplace)) then
                $"TravelCost",                                                          scale (world.TravelCost odInfo) (1.0/float agent.Income)
            
            if dest.IsNonFixed && world.IsSampled then
                ZoneCorrectionParam,                                                    world.Corrections (state.Location, dest)
            
        }
    | Start(activity) -> 
        seq {
            
            //activity-mode dummy
            if Array.contains activity StateSpace.discretionaryActivities then
                $"{activity}_Start_Class{c}",                                           one
            
            //zone-dependent discretionary acts + time-dependent work start
            match activity with 
            | Activity.Shop -> 
                $"Shop_LogEmp_Class{c}",                                                world.LogEmp state.Location
            | Activity.Other -> 
                $"Other_LogPop_Class{c}",                                               world.LogPop state.Location
            | Activity.Work -> 
                yield! interpolate $"Work_Start_Class{c}" 5 3 20 1.0                    (timeToHourOfDay state.TimeOfDay)
            | _ -> ()
            
        }
    | Continue -> 
        let ds = decisionStepMinutes state.TimeOfDay
        seq {

            //activity utility rates
            match state.Activity with
            | Activity.Home -> 
                yield! interpolate $"Home_Continue_Class{c}" 5 3 23 ds                      (timeToHourOfDay state.TimeOfDay)
            | Activity.Work ->
                yield! interpolate $"Work_Continue_Class{c}" 0 3 12 ds                      (timeToHours state.Duration)
            | _ ->
                $"{state.Activity}_Continue_Class{c}",                                      scalar ds
            
            //parking cost
            if not(state.Location.IsResidence) && state.Vehicle = Vehicle.Car then
                $"TravelCost",                                                              scale (world.ParkingRate state.Location) (ds/float agent.Income)
        }
    | End -> Seq.empty


//NB: While this is not the most efficient way to produce utilities, the runtime of the 
//program (for simulation and choiceset generation) is dominated by the EV calculations 
//(e.g. finding and adding the correct EVs from future timesteps, exponentiation) so small 
//inefficiencies here don't really matter.