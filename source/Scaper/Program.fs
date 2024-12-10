open System
open System.IO
open MKLNET
open System.CommandLine
open System.CommandLine.Parsing
open FSharp.SystemCommandLine

// *** NOTE ON PROGRAM.FS *** 
// This is the entry point for the Scaper program. It does not contain any of the Scaper logic itself,
// but instead builds the command line options which the user inputs at runtime, and executes the appropriate
// handler as chosen by the user. See the FSharp.SystemCommandLine Nuget package for details on how it
// is structured. This file should generally only be changed if you are adding a new program option the use
// can run, or if you adding new input options/arguments to existing program options.


//Load the parameters from Utility
let parameters = DataSources.loadParametersWithZoneCorrection Utility.ParameterInputFile

//Command-line options for use in the commands
let degParallel = Input.Option<int>(["--parallelization";"-x"], 1, "The degree of parallelism to use in the program.")
let takeMax = Input.OptionMaybe<int>(["--take"; "-t"], "The maximum number of agents or observations to use. Omit to use all.")
let zoneSampleSize = Input.OptionMaybe<int>(["--zones"; "-z"], "The zone sample size. Omit to use a full sample.")

///Name format for default output and log files
let defaultOutFile (cmd:string) =
    $"{DateTime.Now:``yyMMdd_HHmm``}_{parameters.nClasses}lc_{cmd}"

let outputFile cmd = Input.Option<string>(["--outputFile"; "-o"], 
    fun o -> o.Description <- "Overwrite default filename to output to. File extension optional."
             o.SetDefaultValueFactory(fun () -> defaultOutFile cmd))

///Command for simulation
let simulate = command "sim" {
    description "Simulates new daypaths for the agents"
    inputs (outputFile "sim", degParallel, takeMax, zoneSampleSize)
    setHandler (Simulation.run parameters)
}

//Options for use in the choiceset command only
let numAlternatives = Input.Option<int>(["--alts"; "-a"], 500, "The number of alternative daypaths to simulate for each choiceset.")

///Command for choiceset creation   
let choicesets = command "cs" {
    description "Generates choicesets with sampling of alternatives using simulation"
    inputs (outputFile "cs", numAlternatives, degParallel, takeMax, zoneSampleSize)
    setHandler (Choicesets.run parameters)
}

//Options for use in the estimate command only
let nEstimates = Input.Option<int>(["--nEstimates"; "-n"], 1, "Number of times to repeat estimation. First time will start with given input parameters; subsequent times will randomize starting parameters.")
let numericalH = Input.Option<bool>(["--Hessian"; "-H"], false, "Whether to calculate a numerical Hessian and invert it to calculate parameter errors. Slow especially for many variables. If false (not included), will use Hessian updates from BFGS.")

///Command for zone sampling estimation (subcommand of "est")
let estimateZoneSampling = command "zonesampling" {
    description "Estimates parameters for the MNL model for zone importance sampling"
    inputs (outputFile "zonesampling", takeMax, degParallel, numericalH)
    setHandler ZoneSampling.runEstimation
}

///Command for estimation
let estimate = command "est" {
    description "Estimates new parameters for the current utility function given the choiceset file"
    inputs (outputFile "est", degParallel, nEstimates, numericalH)
    setHandler (Estimation.run parameters)
    addCommand estimateZoneSampling
}

//Options for use in the deriv command only
let ttRange = Input.Option<float[]>(["-tt"],
                                    (fun opt -> opt.AllowMultipleArgumentsPerToken <- true
                                                opt.SetDefaultValue(Array.empty)
                                                opt.Description <- "Travel time range specification (as floats): min, delta, max")) 

let wdRange = Input.Option<int[]>(["-wd"], 
                                  (fun opt -> opt.AllowMultipleArgumentsPerToken <- true
                                              opt.SetDefaultValue(Array.empty)
                                              opt.Description <- "Work duration range specification (as ints): min, delta, max")) 
let numericDeriv = Input.Option<bool>(["--numDeriv"], false, "Report the numerical derivative along with the analytical derivative for verification. (Increases computation time.)")
let doSim = Input.Option<bool>(["--sim"], false, "Simulate average number of work activities and mode distribution along with EV and derivative calculation.")

//Utility changes
let derivative = command "deriv" {
    description "Calculate expected utilities and their derivatives with respect to travel time changes"
    inputs (outputFile "deriv", degParallel, takeMax, zoneSampleSize, ttRange, wdRange, numericDeriv, doSim)
    setHandler (Derivatives.run parameters)
}

//Observed to csv
let obsToCsv = command "obsToCsv" {
    description "Reads the observations and re-writes them to CSV in the Trip output format"
    inputs (outputFile "observed", takeMax)
    setHandler Simulation.obsToCsv
}

///Initializes the program, loading the parameters and land use and network data and printing stats on them
let loadDataSources () =
    printfn $"Running model {DataSources.ModelFolderName} from base folder {DataSources.BaseFolder}"
    let paramFilePath = Path.GetRelativePath(DataSources.BaseFolder, DataSources.findPath DataSources.ParamBaseFolder Utility.ParameterInputFile)
    printfn $"""Parameters file {paramFilePath} loaded with {parameters.nClasses} latent class{if parameters.nClasses > 1 then "es" else ""} and {parameters.estCount} estimable parameters."""   
    LandUse.printStats()
    Network.printStats()
    GC.Collect()


[<EntryPoint>]
let main argv =

    //default to show help message
    let showHelp (ctx: Invocation.InvocationContext) =
        Help.HelpContext(ctx.HelpBuilder, ctx.Parser.Configuration.RootCommand, System.Console.Out)
        |> ctx.HelpBuilder.Write

    //parser for root command
    let parser = rootCommandParser {
        description "Scaper 2 dynamic travel behaviour and scheduling model"
        inputs (Input.Context())
        setHandler showHelp
        addCommand simulate
        addCommand choicesets
        addCommand estimate
        addCommand derivative
        addCommand obsToCsv
    }


    //set options in MKL
    MKL.set_num_threads(1)
    Vml.SetMode(VmlMode.EP) |> ignore


    //options for global use: where is output going
    let conFlag = Option<bool>([|"--console"; "-c"|], (fun () -> false), "Direct output to the console instead of a log file.")
    let logFile = Option<string option>([|"--logFile"; "-l"|], (fun () -> None), "Override the default log file")
    parser.Configuration.RootCommand.AddGlobalOption(conFlag)
    parser.Configuration.RootCommand.AddGlobalOption(logFile)
    
    //parse the command line arguments
    let pr = parser.Parse(argv)

    //if we have a command without errors, do some setup
    if pr.CommandResult <> pr.RootCommandResult &&
       not (pr.CommandResult.Children |> Seq.exists (fun c -> c.Symbol.Name = "help")) &&
       pr.Errors.Count = 0 then

       //make log file for logging output
       Directory.CreateDirectory(DataSources.LogOutputFolder) |> ignore
       let file = Path.ChangeExtension(defaultArg (pr.GetValueForOption logFile) (defaultOutFile pr.CommandResult.Command.Name), "log")
       let filepath = Path.Combine(DataSources.LogOutputFolder, file)
       let logger = new StreamWriter(filepath)
       logger.AutoFlush <- true
       
       //redirect console output to log file (and console if requested)
       let out = match pr.GetValueForOption conFlag with
                 | false -> logger :> TextWriter
                 | true -> new MultiTextWriter(logger, Console.Out)
       Console.SetOut out
       Console.SetError out
              
       //initialize the data sources
       loadDataSources()

    //invoke the command given
    try
        pr.Invoke()
    with
    | e ->
        printfn "%A" e
        raise(e)