Running Scaper from the command line
=================================

This file details the command line options available in the Scaper F# program. The help printouts are also available by running the program with the `-h` option.

## Top-level options

The primary top-level options are:
- `sim`, which generates one daypath for each agent
- `cs`, which generates choicesets to use in estimation
- `est`, which estimates parameters using generated choicesets

Running the program with `-h` provides the following information:

```
Description:
  Scaper 2 dynamic travel behaviour and scheduling model

Usage:
  Scaper [command] [options]

Options:
  --version                Show version information
  -?, -h, --help           Show help and usage information
  -c, --console            Direct output to the console instead of a log file. [default: False]
  -l, --logFile <logFile>  Override the default log file []

Commands:
  sim       Simulates new daypaths for the agents
  cs        Generates choicesets with sampling of alternatives using simulation
  est       Estimates new parameters for the current utility function given the choiceset file
  deriv     Calculate expected utilities and their derivatives with respect to travel time changes
  obsToCsv  Reads the observations and re-writes them to CSV in the Trip output format
```

## Command-specific options

This section provides example usages and the output of the `-h` option when called with a particular command. The `sim`, `cs`, and `est` commands are detailed here; for information on others run the program with the `-h` option.

Example usages are provided assuming the top-level program is called `scaper`. (This can be achieved with an alias in linux, for instance.)

### sim

Example usage: `scaper sim -t 500 -z 100 -x 8` simulates day paths for 500 agents using zone sampling of 100 zones for each agent with up to 8 parallel threads.

```
Description:
  Simulates new daypaths for the agents

Usage:
  Scaper sim [options]

Options:
  -o, --outputFile <outputFile>            Overwrite default filename to output to. File extension optional. [default:
                                           YYMMDD_HHMM_#lc_sim.csv]
  -x, --parallelization <parallelization>  The degree of parallelism to use in the program. [default: 1]
  -t, --take <take>                        The maximum number of agents or observations to use. Omit to use all.
  -z, --zones <zones>                      The zone sample size. Omit to use a full sample.
  -?, -h, --help                           Show help and usage information
  -c, --console                            Direct output to the console instead of a log file. [default: False]
  -l, --logFile <logFile>                  Override the default log file [YYMMDD_HHMM_#lc_sim.log]
```

### cs

Example usage: `scaper cs -z 80 -x 25 -a 250` generates choicesets for all agents in the input data, using zone sampling with 80 zones for each agent and generating 250 alternative daypaths to compare with the observed within each agent's choiceset. It uses up to 25 parallel threads.

```
Description:
  Generates choicesets with sampling of alternatives using simulation

Usage:
  Scaper cs [options]

Options:
  -o, --outputFile <outputFile>           Overwrite default filename to output to. File extension optional. [default:
                                          YYMMDD_HHMM_#lc_cs.parquet]
  -a, --alts <alts>                       The number of alternative daypaths to simulate for each choiceset. [default:
                                          500]
  -x, --parallelization <parallelization> The degree of parallelism to use in the program. [default: 1]
  -t, --take <take>                       The maximum number of agents or observations to use. Omit to use all.
  -z, --zones <zones>                     The zone sample size. Omit to use a full sample.
  -?, -h, --help                          Show help and usage information
  -c, --console                           Direct output to the console instead of a log file. [default: False]
  -l, --logFile <logFile>                 Override the default log file [default: YYMMDD_HHMM_#lc_cs.log]
```

### est

Example usage: `scaper est -x 3` will estimate the currently specified model from the input choicesets using up to 3 parallel threads (note that estimation benefits less from parallelization than simulation and choiceset generation).

```
Description:
  Estimates new parameters for the current utility function given the choiceset file

Usage:
  Scaper est [command] [options]

Options:
  -o, --outputFile <outputFile>           Overwrite default filename to output to. File extension optional. [default:
                                          YYMMDD_HHMM_#lc_est.csv]
  -x, --parallelization <parallelization> The degree of parallelism to use in the program. [default: 1]
  -n, --nEstimates <nEstimates>           Number of times to repeat estimation. First time will start with given input
                                          parameters; subsequent times will randomize starting parameters. [default:
                                          1]
  -H, --Hessian                           Whether to calculate a numerical Hessian and invert it to calculate
                                          parameter errors. Slow especially for many variables. If false (not
                                          included), will use Hessian updates from BFGS. [default: False]
  -?, -h, --help                          Show help and usage information
  -c, --console                           Direct output to the console instead of a log file. [default: False]
  -l, --logFile <logFile>                 Override the default log file [default: YYMMDD_HHMM_#lc_est.log]

Commands:
  zonesampling  Estimates parameters for the MNL model for zone importance sampling
```

The command `est zonesampling`, which estimates zone importance sampling parameters using the input observations' out-of-home non-work activities, has the same options as `est` except `-n`.
