module Estimation

open System
open System.IO
open System.Threading
open Parquet.Serialization
open FSharpx.Option
open Parameters
open Output
open Choicesets
open LogLikelihoodLC
open ZoneSampling
open World
open TripConversion

///Perform the estimation
let run (ps:Parameters) (outputFile:string, degreeOfParallelism:int, numEstimates:int, numericalHessian:bool) = 
    
    //get Parquet file containing choicesets
    let csFilename = if Path.HasExtension outputFile then outputFile else Path.ChangeExtension(Utility.ChoicesetInputFile, "parquet")
    let inputCsPath = DataSources.findPath DataSources.CsBaseFolder csFilename
    printfn $"Choicesets file: {Path.GetRelativePath(DataSources.BaseFolder, inputCsPath)}"

    if ps.estCount = 0 then raise(Exception("No parameters to estimate!"))
    printfn $"""{ps.estCount} parameters to estimate: {String.concat ", " ps.estParams}"""

    // *** DATA LOADING *** //

    let choicesets = ParquetSerializer.DeserializeAsync<Choiceset>(inputCsPath)
                     |> Async.AwaitTask |> Async.RunSynchronously
    let csArr = Array.init choicesets.Count (fun i -> choicesets[i])

    printfn $"\nConverting {csArr.Length} choicesets into estimable format using max {degreeOfParallelism} processes"
    let log = Progress.Logger(csArr.Length, degreeOfParallelism)

    let pool = new ThreadLocal<_>(fun () -> new WorldPool(choicesets[0].Zones.Length))

    //function to pass to the data loader to obtain class utility
    let classDataFunc (cs:Choiceset, c:int) = Utility.classVariables cs.Agent c 
        
    //function to pass to the data loader to obtain choice utility
    let choiceDataFunc (cs:Choiceset, c:int) = 
        let world = 
            if cs.Zones.Length = LandUse.N then fullWorld() 
            else zoneImportanceSample pool.Value cs.Agent cs.Zones.Length cs.Zones
   
        let pathData (alt:Alternative) = maybe {
            let! path = tripListToDayPath cs.Agent alt.Trips
            let vars = path |> Seq.collect (Utility.decisionVariables cs.Agent world c)
            return vars, alt.Correction
        }
        Seq.choose pathData cs.Alternatives |> List.ofSeq
            
    //function to pass to the data loader to obtain agent weights
    let weightFunc (cs:Choiceset) = cs.Agent.Weight
    
    //starting parameter vector
    let defaultParams = ps.estParams |> Array.map ps.getValue
    
    //build the cost function
    let costFunc = makeLCLLCostFunc ps csArr classDataFunc choiceDataFunc weightFunc degreeOfParallelism log.Result
    
    printfn "\nLoaded estimation data; starting estimation."
    
    //manage output directory (add subfolder with time if more than one estimate)
    let outputName = Path.GetFileNameWithoutExtension(outputFile)
    let outputDir = match numEstimates with
                    | 1 -> DataSources.ParamOutputFolder
                    | _ -> let out = outputFile.Split '_' 
                           Path.Combine(DataSources.ParamOutputFolder, if out.Length > 1 then out[1] else $"{DateTime.Now:``HHmm``}")
    Directory.CreateDirectory(outputDir) |> ignore

    
    //number of times to estimate
    for n in 1 .. numEstimates do 

        printfn $"\n\nStarting estimation {n}/{numEstimates}\n"

        //introduce some random variation if more than one estimate requested
        let startParams = Array.copy defaultParams
        if numEstimates > 1 then
            for i in 0 .. (startParams.Length - 1) do
                startParams[i] <- defaultParams[i] * Random.Shared.NextDouble() * 2.0
            
        let suffix = if numEstimates > 1 then $"_{n}" else ""
        let outPath = Path.Combine(outputDir, outputName + suffix + ".csv")
    
        try
            //do maximization
            let _, opt, invHEst = BFGS.maximize costFunc startParams None
            let invH = if numericalHessian then None else Some(invHEst)

            DataSources.writeParamFile ps Utility.ParameterInputFile outPath opt.position.Array

            printEstimationResult ps costFunc opt invH
        with
        | e -> printfn "%A" e