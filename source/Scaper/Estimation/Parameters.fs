module Parameters

open Matrix

///Represents a variable with a parameter name and sequence of Mat values
type Variable = string * Mat seq


///Represents a parameter to be multiplied by variable
type Parameter = {
    Name : string
    Value : float
    Estimate : bool
    EstIndex : int
}

///A parameter for missing values
let NotFoundParameter = {Name="NotFound"; Value=0.0; Estimate=false; EstIndex=(-1)}


///A collection of parameters
type Parameters (parameters:(string*float*bool)[]) =

    let estPs = parameters
                |> Array.filter (fun (_, _, est) -> est)
                |> Array.map (fun (name, _, _) -> name)

    let pDict = parameters 
                |> Array.map (fun (name, value, est) -> 
                        name, {Name=name; Value=value; Estimate=est; EstIndex=(if est then indexIn estPs name else -1)})
                |> dict

    ///A set of parameters that were requested in getParameter or getValue but not found in the parameter set.
    let mutable missingParams : Set<string> = Set.empty
    
    ///The number of latent classes this parameter set contains
    member _.nClasses = 
        match pDict.TryGetValue "nClasses" with
        | true, v -> int v.Value
        | _ -> 1

    
    ///An array of the names of parameters to be estimated
    member _.estParams = estPs

    member _.estCount = estPs.Length
    
    ///Gets the given parameter. If it does not exist, returns 0
    member _.getParameter (name:string) = 
        match pDict.TryGetValue name with
        | true, v -> v
        | _ -> 
            if not(missingParams.Contains name) then
                missingParams <- missingParams.Add name
                printfn $"Possible error: could not find value for parameter {name} in parameter file; using 0 but this may cause problems in the model."
            NotFoundParameter

    ///Gets the value of the given parameter. If it does not exist, returns 0
    member x.getValue (name:string) = (x.getParameter name).Value
