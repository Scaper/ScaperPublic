> [!IMPORTANT]
> You will need to program in F# to build models in Scaper. This document assumes you have some familiarity with F#.

Modifying Scaper's input data
==============

If you are adapting Scaper to a new context, such as a new city or base year, you may need to adjust the code to read in your data, adjust your data to be read by the code, or most likely a bit of both. This is a guide to adjusting the program to new input data. There are also guides to model specification covering changing the [state space](StateSpace.md) and [utility function](Utility.md).

The _DataSources_ module contains the references to input files. In the current implementation, these are in the `input` folder within your model folder. You will need the following four files:
- `zones.csv`: a list of zones with land use data for each zone.
- `network.parquet`: origin-destination matrices for all zone-zone pairs, including at least travel time.
- `agents.csv`: information about the agents to use for simulation or choiceset generation.
- `trips.csv`: the observed trips done by the agents in `agents.csv` to use in choiceset generation. 

Refer to the example input files for how these should be formatted. If you don't want to change your filenames, you can change the hard-coded references in the _DataSources_ module. The discussion below will refer to them by the original filenames.

## Land use data

The _LandUse_ module uses [CSV Type Providers](https://fsprojects.github.io/FSharp.Data/library/CsvProvider.html) to load `zones.csv` in a statically typed way.

> [!NOTE]
> The program internally stores and indexes the zones in the order they are provided in this file. 

The current implementation loads the total population, total employment, and hourly parking rate for each zone. It uses log population and log employment and converts the parking rate to currency per minute. 

If you are only using these variables, the easiest thing to do is to rename the headers in your CSV file to match the example data. However, you can also modify the _LandUse_ module to load your column names instead. For instance, if your column for total population is called _TotalPopulation_, then replace `r.Pop` in the line below with `r.TotalPopulation`. 

```fsharp
let aLogPop = new ThreadLocal<_>(fun () -> getColumn (fun r -> float r.Pop |> max 1.0 |> log))
```

### Adding land use variables

Adding a new land use variable for use in the utility function is also possible. You will need to add to the _LandUse_ module to load the zone data from your CSV file, then modify the _World_ module to make this new data available to the _Utility_ module.

Adding a new variable is not difficult! To illustrate, we will show the code for the population variable. You can copy and paste this code and replace the variable and CSV column names with your new names.

First, the _LandUse_ module loads the column `r.Pop`, sets the minimum value to 1, and takes the log. You should adapt the data transformations to fit your data. The `getColumn` function returns a data array which is stored in a thread-local variable `aLogPop` for efficient parallelization. It also provides a non-thread-local version, `alogPop0`. We'll see how these are used below.

```fsharp
let aLogPop = new ThreadLocal<_>(fun () -> getColumn (fun r -> float r.Pop |> max 1.0 |> log))
let aLogPop0 = aLogPop.Value
```

Next, the _World_ module takes the stored population data and samples it if needed, using the `sampleLandUse` function. Notice that the sampled version uses the non-thread-local version; this is so that the program does not duplicate the data if it only needs it for sampling. If Scaper is run without sampling, having each thread trying to access the same data for each utility function call creates a bottleneck, so the thread-local version is used.

```fsharp
let logPop =
    if isSampled then 
        lock LandUse.aLogPop0 (fun () -> sampleLandUse LandUse.aLogPop0)
    else if overrideThreading then LandUse.aLogPop0 else LandUse.aLogPop.Value
```

Finally, the _World_ module makes the possibly-sampled zone data available for use in the utility function by creating a member on the `World` type. The function `landUseVariable` takes the data array and location and transforms it into the type useful to the utility function (a `Mat seq`). The `zIndex` function transforms the location passed in to its sampled representation if necessary.

```fsharp
member _.LogPop (loc:Location) = landUseVariable logPop (zIndex loc.Zone)
```

Adding your own variable only requires adding modified copies of the above lines of code in the appropriate places. Once this is done, you will be able to use your new variable using the `World` instance passed into the utility function.

## Network data

In Scaper, level of service (LOS) attributes such as travel times and costs are static and exogenous, provided by zone-level origin/destination matrices. The _Network_ module uses [Parquet.Net](https://aloneguid.github.io/parquet-dotnet/) to read network LOS matrices from the file `network.parquet` in Apache Parquet format. 

The file `network.parquet` is assumed to have the same zone order as `zones.csv`. The entire file must be sorted by origin groups, and the rows sorted by destination within each group. If your parquet network file has origins and destinations listed in separate columns, these will be ignored.

> [!IMPORTANT]
> If your network file is not sorted by zone as described above, you will have to re-order it or write new network loading code.

Levels of service are defined by mode for each origin/destination pair. The attributes loaded include travel times, access times, wait times, and travel costs. Not all modes need to have definitions for each attribute:   Levels of service are divided into peak and off-peak and are interpolated in buffers around the peak periods.

The program stores travel, wait and access times in **minutes**. If your data is not in minutes, you need to scale it into minutes by using the transformations described below.

This guide will not cover how to add new network variables; it is similar to the procedure for adding land use variables.

### Modifying network input

You will likely need to adjust how the program loads data from the parquet file. This is done in the _Network_ module in the private functions `ttColumns`, `waitColumns`, `walkAccessColumns` and `costColumns`.

Given a mode and peak boolean (true = peak and false = offpeak), these functions return a map between column names and transformation functions for the column. The _Network_ module will load each column in the map, apply its transformation, and add the results together by zone.

As an example, let's look at the travel cost map definition, shown below. For the Car mode off peak, the program loads two columns: `avst_bil` (the network distance), which is multiplied by a scaling factor of 0.86, and `tull_ovr` (the off-peak congestion toll cost), which is not modified (`id` = the identity function, i.e. do not modify). The total cost is the sum of the scaled distance and the toll. The peak Car mode does the same, but loads `tull_arb` (the peak toll cost). Transit cost is the same peak and off-peak and is taken directly from the `kontanttaxa_Samm` column. All other modes have no related columns; the empty map will produce zero cost values.

```fsharp
let private costColumns (mode:Mode, peak:PeakFlag) =
    match mode, peak with
    | Mode.Car, false -> Map ["avst_bil", (*) 0.86; "tull_ovr", id]
    | Mode.Car, true -> Map ["avst_bil", (*) 0.86; "tull_arb", id]
    | Mode.Transit, _ -> Map ["kontanttaxa_Samm", id]
    | _ -> Map.empty
```

### Changing peak periods

The peak periods are defined near the middle of the _Network_ module. The function `proportionPeak` is used to interpolate between peak and off-peak to avoid sharp jumps between travel times. The interpolation uses a cosine function so the travel times are differentiable, which is important when trying to find marginal utility of travel time.

The following line of code shows that the AM peak period starts at 7am:

```fsharp
let amPeakStart = (7.0 - DayStartHour) * TimeResolutionPerHour
```

## Observation data

The _Observations_ module defines the types `Agent` and `Trip`.

The module uses [CSV Type Providers](https://fsprojects.github.io/FSharp.Data/library/CsvProvider.html) to load `agents.csv` and `trips.csv` in a statically typed way for in simulation. You can of course change the names of these files in the _DataSources_ module. 

The code assumes that `agents.csv` has one agent per row, and `trips.csv` has one trip per row, with an agent's complete schedule across multiple trip rows. If your observation data is contained in one CSV file with agent data repeated across several rows, it is recommended to separate it into two files where the agent file has one row per agent. If you prefer to load agents and trips from the same file, you will have to modify the function `loadAgents` in the _Obervations_ module to filter out duplicates.

For estimation, agents and trips are loaded from serialized choicesets which are stored as Parquet files. The `Agent` and `Trip` types are defined with get/set properties to allow them to be constructed by [Parquet.Net](https://aloneguid.github.io/parquet-dotnet/). It might be tempting to convert them to record types but this will break estimation. This is also the reason the type uses Nullable values instead of the more F#-native option types.

### Modifying agent attributes

The agents you are working with may have different attributes than used in our implementation. These attributes can be used in the state space and the utility function.

To change, add or remove agent attributes you will need to make changes to the _Observations_ module in two places. The first is the definition of the `Agent` type:

```fsharp
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
```

To add a new attribute, add a new `member val ... with get, set` property and a corresponding parameter in the constructor.

The second is the function `agentFromRow` which constructs an `Agent` from a row of `agents.csv`. By using a CSV type provider the row object `r` is statically typed, and you refer to the CSV headings as properties of `r`:

```fsharp
let private agentFromRow (r : AgentCsv.Row) : Agent = 
    Agent(
        indID = r.IndID,
        age = r.Age,
        female = (r.Sex = "f"),
        homeZone = LandUse.zoneIndex[r.Home_zone],
        workZone = (if r.Work_zone.HasValue then Nullable(LandUse.zoneIndex[r.Work_zone.Value]) else Nullable()),
        income = r.Income/1000,
        ownsCar = r.Has_car,
        transitCard = r.Has_ptcard,
        hasKids = r.Has_kids,
        weight = float r.Weight,
        workDuration = Nullable()
    )
```

For adding an attribute, once you have added the property and modified the `Agent` constructor, you will need to modify this function to tell the constructor where in the CSV row `r` the data is coming from. As you can see above, you can modify this data before passing it to the `Agent`: for instance, the income is divided by 1000.

The code `workDuration = Nullable()` sets the work duration to a default value (effectively `null`) as it is not used in the current implementation. This is only possible since `workDuration` was defined above as a `Nullable<int>`.

The CSV type provider infers its static types by scanning the first 1000 rows of the CSV file. This usually determines the correct type, but occasionally the type should be manually specified. The current code does this for the agent's work zone, specifying it as a nullable int using the _Schema_ argument to the CSVProvider type:

```fsharp
type AgentCsv = CsvProvider<DataSources.agentFile, Schema="work_zone=int?">
```

When changing agent attributes, you don't need to change any additional code to make choiceset serialization/deserialization work, as this is handled automatically by Parquet.NET. However, if you add an `option` type to `Agent` then your stored choicesets will not load properly. Also, be aware that if you try to estimate a model using choicesets produced before a change to `Agent`, the code will load the older version of the agent stored in the choicesets and will not include the updated data.

### Adjusting for your trip data

Like agent data, observed trip data is loaded using a statically-typed CSV type provider row object with properties determined by the headers in `trips.csv`.  This is done by the function `tripFromRow`:

```fsharp
let private tripFromRow (r : TripCsv.Row) : Trip = 
    Trip(
        indID = r.IndID,
        activity = Activity.Parse(r.Activity),
        mode = Mode.Parse(r.Mode),
        origin = r.Origin,
        destination = r.Destination,
        departureTime = TimeOnly.FromTimeSpan(r.DepartureTime)
    )
```

You can adjust your column names in `trips.csv` to match the program's expected column names, or change this function to accommodate your data structure.

The enum parsing (e.g. `Mode.Parse(r.Mode)`) takes a string matching the enum case defined in the _Definitions_ module, which requires your data to match the defined enum cases exactly. If your data is formatted differently you can define the conversion in this function.

The trip constructor does not take travel time as an input, since the travel time (and wait and access time if applicable) are derived from the network data. In the current code it is not possible to overwrite the travel times from the _Network_ module.

It should not usually be necessary to add or remove trip attributes, which are usually fairly standard across models (i.e. origin, destination, mode, activity purpose, departure time). However, changing the `Trip` type can be done in a similar way to the `Agent` type as discussed above. One caveat is that the _TripConversion_ module will also need to be adjusted to update how trips are converted into paths through the state space.
