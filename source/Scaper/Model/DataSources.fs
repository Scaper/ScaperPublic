[<RequireQualifiedAccess>]
module DataSources

open System
open System.IO
open System.Globalization
open FSharp.Data
open CsvHelper
open Parameters

///The name of the folder which collects all the data (inputs, outputs, logs) about the
///model currently in use.
[<Literal>]
let ModelFolderName = "UmeaExample"


///The path to the base folder to find data. Currently assumes there is a folder
///called "models" in the same directory as the Solution root directory, and
///which contains a folder with the model folder name given above
[<Literal>]
let private baseFolder = __SOURCE_DIRECTORY__ + "/../../../models/" + ModelFolderName + "/"
let BaseFolder = Path.GetFullPath(baseFolder)


// *** INPUT DATA *** //

[<Literal>]
let private inputFolder = baseFolder + "input/"
let InputFolder = Path.GetFullPath(inputFolder)

///The source of land use data by zone
[<Literal>]
let zoneFile = inputFolder + "landuse.csv"

///The source of the transport network data (i.e. levels of service by O/D)
[<Literal>]
let networkFile = inputFolder + "network.parquet"

///The source of agents to use for simulation or choiceset generation
[<Literal>]
let agentFile = inputFolder + "agents.csv"

///The source of agent observed trips to use in choiceset generation
[<Literal>]
let tripFile = inputFolder + "trips.csv"

///A template for parameter values
[<Literal>]
let paramTemplateFile = inputFolder + "param_template.csv"


// *** OUPUT FOLDERS *** //

///Returns path to an output folder for the given name, with date as a subfolder.
let outputFolder name = Path.Combine(BaseFolder, name, $"{DateTime.Now:``yy-MM-dd``}") 

///The folder path for simulation output
let SimOutputFolder = outputFolder "sim"

///The folder path for choiceset output
let CsOutputFolder = outputFolder "cs"

///The folder path for log output
let LogOutputFolder = outputFolder "logs"

///The folder path for parameter estimation output
let ParamOutputFolder = outputFolder "est"



// *** OUTPUT WRITERS *** //

let getSimCsvWriter (outputFile:string) = 
    let outPath = Path.Combine(SimOutputFolder, Path.ChangeExtension(outputFile, "csv"))
    Directory.CreateDirectory(SimOutputFolder) |> ignore
    let w = new StreamWriter(outPath)
    new CsvWriter(w, CultureInfo.InvariantCulture)

// *** BASE FOLDERS *** //

//The base folder in which all choiceset outputs from various dates are kept
let CsBaseFolder = Path.Combine(BaseFolder, "cs")

///The base folder in which all parameter estimation outputs from various dates are kepts
let ParamBaseFolder = Path.Combine(BaseFolder, "est")

let findPath (baseFolderPath:string) (filename:String) : string = 
    let files = Directory.GetFiles(baseFolderPath, filename, SearchOption.AllDirectories)
    if files.Length = 0 then
        raise(Exception $"Cannot find input file {filename} in {baseFolderPath} or its subfolders.")
    if files.Length > 1 then 
        printfn $"Warning: the file {filename} has more than one match in {baseFolderPath} and its subfolders. Using the first match."
    files[0]


// *** PARAMETER LOADING AND WRITING *** //

type ParamCsv = CsvProvider<paramTemplateFile>


let private loadParams (paramFile:string) (zoneCorrection:bool) =
    let path = findPath ParamBaseFolder paramFile

    (ParamCsv.Load path).Rows
    |> Seq.map (fun r -> r.Parameter, float r.Value, r.Estimate)
    |> Seq.append (if zoneCorrection then [(ZoneCorrectionParam, 1.0, false)] else [])
    |> Seq.toArray
    |> Parameters

///Load the parameters from the given file, not including a zone correction parameter.
let loadParameters paramFile = loadParams paramFile false

///Load the parameters from the given file, including a zone correction parameter.
let loadParametersWithZoneCorrection paramFile = loadParams paramFile true

/// Write the new parameters (and fixed ones) to an output file
let writeParamFile (ps:Parameters) (paramFile:string) (outFilepath:string) (estParamVals:float[]) =
    let path = findPath ParamBaseFolder paramFile
    
    let updateEst (row:ParamCsv.Row) = 
        let p = ps.getParameter row.Parameter
        if p.Estimate && row.Estimate
            then decimal estParamVals[p.EstIndex]
            else row.Value
    
    (ParamCsv.Load path).Map(fun row -> ParamCsv.Row(row.Parameter, updateEst row, row.Estimate))
                        .Save(outFilepath)
    