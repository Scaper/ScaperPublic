[<RequireQualifiedAccess>]
module Progress

open System
open System.Diagnostics
open System.Threading
open System.Collections.Concurrent




///Class for logging progress of repeated computations and printing information on the progress including the estimated time remaining.
type Logger(total:int, nProcesses:int) = 
    
    let mutable failures = 0
    let mutable successes = 0
    let mutable lastMessagePrinted = TimeSpan(0)

    do printfn "%s" ("    Progress    |  Fails  |" + (if nProcesses > 1 then "  Threads  |" else "") + "  Avg time " + (if nProcesses > 1 then "local/global " else "") + " |  Elapsed  |  Remaining  |  Total")

    //for counting threads used
    let c = Counter()
    let thrd = new ThreadLocal<_>(fun () -> c.Next)
    let threads = new ConcurrentQueue<int>()


    let timer = Stopwatch()
    do timer.Start()

    //internal function to log a success or failure and print out messages
    let log (flag:bool) = 

        //record that we have had a log from the calling thread
        threads.Enqueue(thrd.Value)
        if threads.Count > (4*nProcesses) then threads.TryDequeue() |> ignore

        if flag then successes <- successes + 1 
                else failures <- failures + 1

        let sinceLastMessage = timer.Elapsed - lastMessagePrinted

        //consider printing out a message every 30 seconds or if we're done
        if (sinceLastMessage.TotalSeconds >= 30 || successes + failures >= total) then 

            let nProcessesActual = threads.ToArray() |> Set.ofArray |> Set.count

            //calculate average times, trying to account for tasks in progress by assuming the unfinished ones from this round are 75% done
            let completePerProcessor = float (successes + failures) / float nProcessesActual
            let avgTimeProcessor = timer.Elapsed.TotalSeconds / (0.25 * completePerProcessor + (0.75 * (ceil completePerProcessor)))
            let avgTimeGlobal = avgTimeProcessor / float nProcessesActual
            let nRemaining = total - successes - failures
            let estRemaining = TimeSpan(0, 0, int (avgTimeGlobal * float nRemaining + (avgTimeProcessor * min 1.0 (float nRemaining)))) //try to account for late finishers
            let estTotal = timer.Elapsed + estRemaining
        
            //output summary of what has been done so far (try for about 10 throughout the process, with at least one per hour)
            if (lastMessagePrinted.Ticks = 0 || sinceLastMessage >= estTotal/10.0 || sinceLastMessage.TotalHours >= 1 || successes + failures >= total) then
                printfn "%s"
                    ($"{successes + failures,6} / {total,-6} |  {failures,4}   " + 
                    (if nProcesses > 1 then $"|    {nProcessesActual,3}    " else "") +
                    $"|  {avgTimeProcessor,6:F2}s " + 
                    (if nProcesses > 1 then $" / {avgTimeGlobal,7:F4}s  " else "") + 
                    $"  |  {int timer.Elapsed.TotalHours}{timer.Elapsed:``\:mm\:ss``}  |   {(int)estRemaining.TotalHours}{estRemaining:``\:mm\:ss``}   | {(int)estTotal.TotalHours}{estTotal:``\:mm\:ss``}")
                lastMessagePrinted <- timer.Elapsed

    ///Log a result as successful (true) or not (false)
    member x.Result success = lock (x) (fun () -> log success)