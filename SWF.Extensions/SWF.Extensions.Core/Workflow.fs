﻿namespace Amazon.SimpleWorkflow.Extensions

open System
open System.Collections.Generic

open Amazon.SimpleWorkflow
open Amazon.SimpleWorkflow.Model
open ServiceStack.Text

open Amazon.SimpleWorkflow.Extensions
open Amazon.SimpleWorkflow.Extensions.Model

#nowarn "25"

// #region Interface Definitions

// Marker interface for anything that can be scheduled at a stage of a workflow
type ISchedulable = 
    abstract member Name        : string
    abstract member Description : string
    abstract member MaxAttempts : int
    abstract member Input       : Input option

// Represents an activity
type IActivity =
    inherit ISchedulable

    abstract member Version                     : string option
    abstract member TaskList                    : TaskList    
    abstract member TaskHeartbeatTimeout        : Seconds
    abstract member TaskScheduleToStartTimeout  : Seconds
    abstract member TaskStartToCloseTimeout     : Seconds
    abstract member TaskScheduleToCloseTimeout  : Seconds
    
    /// Processes a string input and returns the result
    abstract member Process     : string -> string

// Represents a workflow
type IWorkflow = 
    inherit ISchedulable

    abstract member TaskList                     : TaskList
    abstract member Version                      : string
    abstract member TaskStartToCloseTimeout      : Seconds option
    abstract member ExecutionStartToCloseTimeout : Seconds option
    abstract member ChildPolicy                  : ChildPolicy option

    abstract member Start       : IAmazonSimpleWorkflow -> string -> unit

// #endregion

type SchedulableState =
    {
        mutable StageNumber   : int
        mutable AttemptNumber : int
        mutable MaxAttempts   : int
        mutable ActionNumber  : int     // zero-based index of the action
        mutable TotalActions  : int     // total number of actions scheduled together
    }

/// Reducer is a function that takes a dictionary of action number and action result and reduce
/// them into a single string as an aggregated result for the next activity/child workflow
/// in the current workflow
type Reducer = Dictionary<int, string> -> string

/// The different actions that can be taken as a stage in the workflow
type StageAction = 
    | ScheduleActivity      of IActivity
    | StartChildWorkflow    of IWorkflow
    // ParallelActions args :
    //      the list of ISchedulable actions to schedule
    //      reducer to aggregate results from all the scheduled actions into a single result
    | ParallelActions       of ISchedulable[] * Reducer

/// Represents a stage in the workflow
type Stage =
    {
        StageNumber : int           // zero-based index, e.g. 5 = 6th stage
        Action      : StageAction   // what action to perform at this stage in the workflow?
    }

/// Represents the state of a workflow
type WorkflowState =
    {
        mutable CurrentStageNumber  : int   // zero-based index of the currently executing stage
        mutable NumberOfActions     : int   // number of actions scheduled at this stage
        mutable Results             : Dictionary<int, string>   // the results from the actions so far
    }

[<AutoOpen>]
module WorkflowUtils =
    let private schedulableStateSerializer = JsonSerializer<SchedulableState>()
    let private workflowSerializer         = JsonSerializer<WorkflowState>()

    /// Serialize/Deserialize a SchedulableState to and from string
    let serializeSchedulableState   = schedulableStateSerializer.SerializeToString
    let deserializeSchedulableState = schedulableStateSerializer.DeserializeFromString

    /// Serialize/Deserialize a WorkflowState to and from string
    let serializeWorkflowState      = workflowSerializer.SerializeToString
    let deserializeWorkflowState    = workflowSerializer.DeserializeFromString

    /// Returns the scheduled action given the action's current state and the stage of workflow it's at
    let getScheduled state = function
        | { Action = ScheduleActivity(activity) }       -> activity :> ISchedulable
        | { Action = StartChildWorkflow(workflow) }     -> workflow :> ISchedulable
        | { Action = ParallelActions(schedulables, _) } -> schedulables.[state.ActionNumber]

    /// Identifies the key events which we need to respond to
    let (|KeyEvent|_|) historyEvt = 
        match historyEvt with
        | { EventType = ActivityTaskFailed _ } 
        | { EventType = ActivityTaskTimedOut _ } 
        | { EventType = ActivityTaskCompleted _ }
        | { EventType = ChildWorkflowExecutionFailed _ } 
        | { EventType = ChildWorkflowExecutionTimedOut _ } 
        | { EventType = ChildWorkflowExecutionCompleted _ }
        | { EventType = StartChildWorkflowExecutionInitiated _ }
        | { EventType = WorkflowExecutionStarted _ }
            -> Some historyEvt.EventType
        | _ -> None

    /// Returns the result from a scheduled action
    let (|ActionResult|_|) = function
        | { EventType = ActivityTaskCompleted(initEvtId, _, result) }
        | { EventType = ChildWorkflowExecutionCompleted(initEvtId, _, _, _, result) } 
            -> Some <| (initEvtId, result)
        | _ -> None

    /// Given an array of ISchedulable, returns the activities
    let getActivities (arr : ISchedulable[]) = arr |> Array.choose (function | :? IActivity as activity -> Some activity | _ -> None)

    /// Given an array of ISchedulable, returns the workflows
    let getWorkflows (arr : ISchedulable[])  = arr |> Array.choose (function | :? IWorkflow as workflow -> Some workflow | _ -> None)

    let private random = new Random()

    /// Formats the activity ID for an activity given its current state
    let getActionId actionName { StageNumber = stageNum; AttemptNumber = attempts; ActionNumber = actionNum } = 
        sprintf "%s.stag_%d.act_%d.attempt_%d.time_%s.%d" actionName stageNum actionNum attempts (DateTime.Now.ToString("yyyy.MM.ddTHH:mm:ss:fff")) (random.Next())

    /// Formats the activity ID for an activity given its current state
    let getChildWorkflowId actionName state = 
        sprintf "%s.%O" (getActionId actionName state) (Guid.NewGuid())

    /// Formats the activity version for an activity at a particular stage of a workflow
//    let getActivityVersion workflowName workflowVersion stageNum (activity : IActivity) = 
//        match activity.Version with
//        | Some v -> sprintf "%s.v%s.%d.v%s" workflowName workflowVersion stageNum v
//        | _      -> sprintf "%s.v%s.%d" workflowName workflowVersion stageNum
    
    /// Returns the event type associated with the specified event ID
    let findEventTypeById eventId (evts : HistoryEvent seq) =
        evts |> Seq.pick (function 
            | { EventId = eventId'; EventType = eventType } when eventId = eventId' 
                -> Some eventType 
            | _ -> None)

    /// Returns the results from schedulable actions (activity & workflow) for a given stage number
    let getStageResults stageNum (evts : HistoryEvent seq) =
        evts 
        |> Seq.choose (function 
            | ActionResult (initEvtId, result) ->
                // get the action number from the state of the scheduled action
                let initEvtType = findEventTypeById initEvtId evts
                match initEvtType with
                | ActivityTaskScheduled(_, _, _, _, Some control, _, _, _, _, _)
                | StartChildWorkflowExecutionInitiated(_, _, _, _, _, _, _, Some control, _, _)
                    -> let state = deserializeSchedulableState control
                       Some(state.StageNumber, state.ActionNumber, result)
                | _ -> None
            | _ -> None)
        |> Seq.takeWhile (fun (stageNum', _, _) -> stageNum' = stageNum)
        |> Seq.toArray

    /// Returns the decision to schedule an activity
    let scheduleActivity actionNum totalActions workflowName workflowVersion stageNum (activity : IActivity) input attempts = 
        let activityType = ActivityType(Name = activity.Name, Version = defaultArg activity.Version "1.0") //getActivityVersion workflowName workflowVersion stageNum activity)
        let state        = { 
                                StageNumber   = stageNum
                                AttemptNumber = attempts + 1
                                MaxAttempts   = activity.MaxAttempts
                                ActionNumber  = actionNum
                                TotalActions  = totalActions
                            }
        let control      = serializeSchedulableState state
        let decision     = ScheduleActivityTask(getActionId activity.Name state, 
                                                activityType,
                                                Some activity.TaskList, 
                                                input,
                                                secondsOp activity.TaskHeartbeatTimeout, 
                                                secondsOp activity.TaskScheduleToStartTimeout, 
                                                secondsOp activity.TaskStartToCloseTimeout, 
                                                secondsOp activity.TaskScheduleToCloseTimeout,
                                                Some control)
        decision

    /// Returns the decision to schedule a stage with a single activity
    let scheduleActivityStage = scheduleActivity 0 1

    /// Returns the decision to schedule a child workflow
    let scheduleChildWorkflow actionNum totalActions stageNum (workflow : IWorkflow) input attempts =
            let schedulable  = workflow :> ISchedulable
            let workflowType = WorkflowType(Name = schedulable.Name, Version = workflow.Version)
            let state        = { 
                                    StageNumber   = stageNum
                                    AttemptNumber = attempts + 1
                                    MaxAttempts   = schedulable.MaxAttempts
                                    ActionNumber  = actionNum
                                    TotalActions  = totalActions
                               }
            let control      = serializeSchedulableState state
            let decision     = StartChildWorkflowExecution(getChildWorkflowId schedulable.Name state, 
                                                           workflowType,
                                                           workflow.ChildPolicy,
                                                           Some workflow.TaskList, 
                                                           input,
                                                           None,
                                                           workflow.ExecutionStartToCloseTimeout,
                                                           workflow.TaskStartToCloseTimeout,                                                             
                                                           Some control)
            decision

    /// Returns the decision to schedule a stage with a single child workflow
    let scheduleChildWorkflowStage = scheduleChildWorkflow 0 1

// #region Activity Definition

/// Represents an activity, which in essence, is a function that takes some input and 
/// returns some output
type Activity<'TInput, 'TOutput>(name, description, 
                                 taskHeartbeatTimeout        : Seconds,
                                 taskScheduleToStartTimeout  : Seconds,
                                 taskStartToCloseTimeout     : Seconds,
                                 taskScheduleToCloseTimeout  : Seconds,
                                 ?version,
                                 ?taskList,
                                 ?maxAttempts,
                                 ?input,
                                 ?processor                  : 'TInput -> 'TOutput) =
    let taskList    = defaultArg taskList (name + "TaskList")
    let maxAttempts = defaultArg maxAttempts 1    // by default, only attempt once, i.e. no retry

    let inputSerializer  = JsonSerializer<'TInput>()
    let outputSerializer = JsonSerializer<'TOutput>()

    let mutable processor = defaultArg processor (fun input -> Unchecked.defaultof<'TOutput>)

    // use Json serializer to marshall the input and output from-and-to string
    member this.Processor 
        with get() = processor
        and set v = processor <- v
    
    interface IActivity with
        member this.Name                        = name
        member this.Version                     = version
        member this.Description                 = description
        member this.TaskList                    = TaskList(Name = taskList)
        member this.TaskHeartbeatTimeout        = taskHeartbeatTimeout
        member this.TaskScheduleToStartTimeout  = taskScheduleToStartTimeout
        member this.TaskStartToCloseTimeout     = taskStartToCloseTimeout
        member this.TaskScheduleToCloseTimeout  = taskScheduleToCloseTimeout
        member this.MaxAttempts                 = maxAttempts
        member this.Input                       = input

        member this.Process input =
            inputSerializer.DeserializeFromString >> this.Processor >> outputSerializer.SerializeToString <| input 

type Activity = Activity<string, string>

// #endregion

type Workflow (domain, name, description, version, 
               ?taskList, 
               ?stages                  : Stage list,
               ?taskStartToCloseTimeout : Seconds,
               ?execStartToCloseTimeout : Seconds,
               ?childPolicy             : ChildPolicy,
               ?identity                : Identity,
               ?maxAttempts,
               ?input) =
    do if nullOrWs domain then nullArg "domain"
    do if nullOrWs name   then nullArg "name"

    //let input = defaultArg input Unchecked.defaultof<Input>
    let taskList = new TaskList(Name = defaultArg taskList (name + "TaskList"))
    let maxAttempts = defaultArg maxAttempts 1    // by default, only attempt once, i.e. no retry

    let onDecisionTaskError = new Event<Exception>()
    let onActivityTaskError = new Event<Exception>()
    let onActivityFailed    = new Event<Domain * Name * ActivityId * Details option * Reason option>()
    let onWorkflowFailed    = new Event<Domain * Name * RunId * Details option * Reason option>()
    let onWorkflowCompleted = new Event<Domain * Name>()

    // sort the stages by Id
    let stages = defaultArg stages [] |> List.sortBy (fun { StageNumber = n } -> n)

    // all the activities (including those part of a schedule multiple action)
    let activities = stages |> List.collect (function 
            | { Action = ScheduleActivity(activity) } -> [ activity ]
            | { Action = ParallelActions(arr, _) }    -> arr |> getActivities |> Array.toList
            | _ -> [])

    // all the child workflows (including those part of a schedule multiple action)
    let childWorkflows = stages |> List.collect (function 
            | { Action = StartChildWorkflow(workflow) } -> [ workflow ]
            | { Action = ParallelActions(arr, _) }      -> arr |> getWorkflows |> Array.toList
            | _ -> [])
    
    // tries to get the nth (zero-index) stage
    let getStage n = if n >= stages.Length then None else List.item n stages |> Some

    // registers the workflow and activity types
    let register (clt : IAmazonSimpleWorkflow) = 
        let registerActivities stages = async {
            let req  = ListActivityTypesRequest(Domain = domain, RegistrationStatus = swfRegStatus Registered)
            let! res = clt.ListActivityTypesAsync(req) |> Async.AwaitTask

            let existing = res.ActivityTypeInfos.TypeInfos
                           |> Seq.map (fun info -> info.ActivityType.Name, info.ActivityType.Version)
                           |> Set.ofSeq

            let activities = stages 
                             |> List.collect (function 
                                | { StageNumber = stageNum; Action = ScheduleActivity(activity) }
                                    -> [ (stageNum, activity, version)] //getActivityVersion stageNum activity) ]
                                | { StageNumber = stageNum; Action = ParallelActions(arr, _) }
                                    -> //let getActivityVersion = getActivityVersion stageNum
                                       arr 
                                       |> getActivities 
                                       |> Array.map (fun activity -> stageNum, activity, version (*getActivityVersion activity*) ) 
                                       |> Array.toList
                                | _ -> [])
                             |> List.filter (fun (_, activity, version) -> not <| existing.Contains(activity.Name, version))
                             |> Seq.distinctBy (fun (_, activity, version) -> activity.Name, version)

            for (id, activity, version) in activities do
                let req = RegisterActivityTypeRequest(
                            Domain          = domain, 
                            Name            = activity.Name,
                            Description     = activity.Description,
                            DefaultTaskList = activity.TaskList,
                            Version         = version,
                            DefaultTaskHeartbeatTimeout         = secondsStr activity.TaskHeartbeatTimeout,
                            DefaultTaskScheduleToStartTimeout   = secondsStr activity.TaskScheduleToStartTimeout,
                            DefaultTaskStartToCloseTimeout      = secondsStr activity.TaskStartToCloseTimeout,
                            DefaultTaskScheduleToCloseTimeout   = secondsStr activity.TaskScheduleToCloseTimeout)

                do! clt.RegisterActivityTypeAsync(req) |> Async.AwaitTask |> Async.Ignore
        }

        let registerWorkflow () = async {
            let req = ListWorkflowTypesRequest(Domain = domain, Name = name, RegistrationStatus = swfRegStatus Registered)
            let! res = clt.ListWorkflowTypesAsync(req) |> Async.AwaitTask

            // only register the workflow if it doesn't exist already
            if res.WorkflowTypeInfos.TypeInfos.Count = 0 then
                let req = RegisterWorkflowTypeRequest(
                            Domain          = domain, 
                            Name            = name,
                            Description     = description,
                            Version         = version,
                            DefaultTaskList = taskList)
                let taskStartClose = secondsStrOp taskStartToCloseTimeout                
                <@ req.DefaultTaskStartToCloseTimeout @>      <-? secondsStrOp taskStartToCloseTimeout
                <@ req.DefaultExecutionStartToCloseTimeout @> <-? secondsStrOp execStartToCloseTimeout
                <@ req.DefaultChildPolicy @>                  <-? (childPolicy ?>> swfChildPolicy)

                do! clt.RegisterWorkflowTypeAsync(req) |> Async.AwaitTask |> Async.Ignore
        }

        let registerDomain () = async {
            let req = ListDomainsRequest(RegistrationStatus = swfRegStatus Registered)
            let! res = clt.ListDomainsAsync(req) |> Async.AwaitTask

            // only register the domain if it doesn't exist already
            let exists = res.DomainInfos.Infos |> Seq.exists (fun info -> info.Name = domain)
            if not <| exists then
                let req = RegisterDomainRequest(
                            Name = domain,
                            WorkflowExecutionRetentionPeriodInDays = string Constants.maxWorkflowExecRetentionPeriodInDays)
                do! clt.RegisterDomainAsync(req) |> Async.AwaitTask |> Async.Ignore
        }

        // run the domain registration first, otherwise the activities and workflow registrations will fail!
        registerDomain() |> Async.RunSynchronously

        seq { yield registerActivities stages; yield registerWorkflow();  }
        |> Async.Parallel
        |> Async.RunSynchronously
    
    // schedules the nth (zero-indexed) stage given an input and current number of attempts
    let scheduleStage stageNum input attempts =        
        match getStage stageNum with
        | Some({ Action = ScheduleActivity(activity) }) 
               -> let input' = 
                    match activity.Input with
                    | None -> input
                    | Some _ -> activity.Input                
                  [| scheduleActivityStage name version stageNum activity input' attempts |],
                  serializeWorkflowState { CurrentStageNumber = stageNum; NumberOfActions = 1; Results = new Dictionary<int, string>() }
        | Some({ Action = StartChildWorkflow(workflow) }) 
               -> [| scheduleChildWorkflowStage stageNum workflow input attempts |], 
                  serializeWorkflowState { CurrentStageNumber = stageNum; NumberOfActions = 1; Results = new Dictionary<int, string>() }
        | Some({ Action = ParallelActions(actions, _) })
               -> let totalActions = actions.Length
                  
                  // collect all the decisions required to schedule the stage
                  let decisions = actions |> Array.mapi (fun i x -> 
                      match x with
                      | :? IActivity as activity -> 
                        let input' = 
                            match activity.Input with
                            | None -> input
                            | Some _ -> activity.Input
                        scheduleActivity i totalActions name version stageNum activity input' attempts
                      | :? IWorkflow as workflow -> scheduleChildWorkflow i totalActions stageNum workflow input attempts)

                  decisions, serializeWorkflowState { CurrentStageNumber = stageNum; NumberOfActions = totalActions; Results = new Dictionary<int, string>() }
        | None -> onWorkflowCompleted.Trigger(domain, name)
                  [| CompleteWorkflowExecution input |], ""

    let decide (swfClient : IAmazonSimpleWorkflow, task : DecisionTask) =
        let getLastExecContext () =
            let req = DescribeWorkflowExecutionRequest(Domain = domain, Execution = task.WorkflowExecution)
            let res = swfClient.DescribeWorkflowExecution(req)
            res.WorkflowExecutionDetail.LatestExecutionContext

        // makes the next move based on the control data for the previous step and its result
        let nextStep (Some control) result =
            let state = deserializeSchedulableState control

            // if there are more than one action scheduled in parallel, then check if they're all completed before
            // attempting to schedule the next stage
            if state.TotalActions > 1 
            then
                let wfState = deserializeWorkflowState <| getLastExecContext()
                
                // get the results from the stage so far and update the results of the workflow
                let stageResults = getStageResults state.StageNumber task.Events
                stageResults |> Array.iter (fun (_, actionNum, result) -> 
                    wfState.Results.[actionNum] <- defaultArg result Unchecked.defaultof<string>)

                // if all the results are in, then aggregate them with the reducer and schedule the next stage
                // otherwise, do nothing, and simply update the current workflow
                if wfState.Results.Count = wfState.NumberOfActions 
                then
                    let (Some stage) = getStage state.StageNumber
                    match stage with
                    | { Action = ParallelActions(_, reduce) } ->
                        let result = reduce wfState.Results
                        scheduleStage (state.StageNumber + 1) (Some result) 0
                    | _ -> failwithf "Workflow %s's stage %d is expected to be ParallelActions" name state.StageNumber                        
                else [||], serializeWorkflowState wfState
            else scheduleStage (state.StageNumber + 1) result 0

        // defailed wheter to fail the workflow or retry a failed action
        let retryAction (Some control) actionId input details reason =
            let state = deserializeSchedulableState control

            // get the scheduled action that failed, raise the appropriate event
            let (Some stage) = getStage state.StageNumber
            let schedulable = getScheduled state stage
            match schedulable with
            | :? IActivity as activity -> onActivityFailed.Trigger(domain, activity.Name, actionId, details, reason)
            | :? IWorkflow as workflow -> onWorkflowFailed.Trigger(domain, workflow.Name, actionId, details, reason)
            | x -> failwithf "Unsuppoted schedulable: %A" x

            if state.AttemptNumber >= state.MaxAttempts
            then [| FailWorkflowExecution(details, reason) |], ""
            else 
                // we need to retry the failed ISchedulable
                let decision = match schedulable with
                               | :? IActivity as activity -> 
                                   scheduleActivity state.ActionNumber state.TotalActions name version state.StageNumber activity input state.AttemptNumber
                               | :? IWorkflow as workflow -> 
                                   scheduleChildWorkflow state.ActionNumber state.TotalActions state.StageNumber workflow input state.AttemptNumber
                [| decision |], getLastExecContext()

        let events = task.Events
        let keyEvt = events |> Seq.pick (function | KeyEvent evtType -> Some evtType | _ -> None)

        match keyEvt with
        | WorkflowExecutionStarted(_, _, _, _, _, _, _, input, _, _) -> scheduleStage 0 input 0

        // when activities and workflows completed/failed, we need to go back to the event that scheduled them in order
        // to get the control data we need to find out the state of that stage of the workflow in order to decide
        // on the appropriate next course of action
        | ActivityTaskFailed(scheduledEvtId, _, details, reason) ->
            let (ActivityTaskScheduled(activityId, _, _, _, control, input, _, _, _, _)) = findEventTypeById scheduledEvtId events
            retryAction control activityId input details reason

        | ActivityTaskTimedOut(scheduledEvtId, _, timeoutType, details) ->
            let (ActivityTaskScheduled(activityId, _, _, _, control, input, _, _, _, _)) = findEventTypeById scheduledEvtId events            
            retryAction control activityId input details (Some(sprintf "Timeout : %s" <| str timeoutType))

        | ActivityTaskCompleted(scheduledEvtId, _, result) ->
            let (ActivityTaskScheduled(_, _, _, _, control, _, _, _, _, _)) = findEventTypeById scheduledEvtId events
            nextStep control result

        | ChildWorkflowExecutionFailed(initiatedEvtId, _, workflowExec, _, details, reason) ->            
            let (StartChildWorkflowExecutionInitiated(_, _, _, _, _, _, input, control, _, _)) = findEventTypeById initiatedEvtId events
            retryAction control workflowExec.RunId input details reason

        | ChildWorkflowExecutionTimedOut(initiatedEvtId, _, workflowExec, _, timeoutType) ->
            let (StartChildWorkflowExecutionInitiated(_, _, _, _, _, _, input, control, _, _)) = findEventTypeById initiatedEvtId events
            retryAction control workflowExec.RunId input None (Some(sprintf "Timeout : %s" <| str timeoutType))

        | ChildWorkflowExecutionCompleted(initiatedEvtId, _, _, _, result) ->
            let (StartChildWorkflowExecutionInitiated(_, _, _, _, _, _, _, control, _, _)) = findEventTypeById initiatedEvtId events
            nextStep control result

        | StartChildWorkflowExecutionInitiated _ ->
            // we're still waiting for a child workflow to finish, do nothing for now
            [||], getLastExecContext()

    let startWorkflow (clt : IAmazonSimpleWorkflow) id =
        
//        let listWorkflowRequest = 
//            new ListOpenWorkflowExecutionsRequest(
//                Domain = domain,
//                TypeFilter = new WorkflowTypeFilter (Name = name, Version = version),
//                StartTimeFilter =
//                    new ExecutionTimeFilter (OldestDate = DateTime.Now.AddDays -14., LatestDate = DateTime.Now)
//                )
//
//        if (clt.ListOpenWorkflowExecutions(listWorkflowRequest).WorkflowExecutionInfos.ExecutionInfos
//            |> Seq.exists (fun x -> not x.CancelRequested) ) then
        new StartWorkflowExecutionRequest (
            Input = defaultArg input String.Empty,
            WorkflowId = id,
            Domain = domain,
            WorkflowType = new WorkflowType(
                Name = name,
                Version = version
            ),
            TaskList = taskList
        )
        |> clt.StartWorkflowExecution
        |> ignore
        
    let startDecisionWorker clt  = DecisionWorker.Start(clt, domain, taskList.Name, decide, onDecisionTaskError.Trigger, ?identity = identity)
    let startActivityWorkers clt = 
        activities
        |> List.iter (fun activity -> 
            // leave some buffer for heart beat frequency, record a heartbeat every 75% of the timeout
            let heartbeat = TimeSpan.FromSeconds(float activity.TaskHeartbeatTimeout * 0.75)
            ActivityWorker.Start(clt, domain, activity.TaskList.Name, activity.Process, onActivityTaskError.Trigger, heartbeat, ?identity = identity))
    let startChildWorkflows clt id = childWorkflows |> List.iter (fun workflow -> workflow.Start clt id)

    let start clt id = 
        register clt |> ignore
        startWorkflow clt id
        startDecisionWorker clt
    let startWorkers clt id = 
        //register clt |> ignore
        startActivityWorkers clt
        startChildWorkflows  clt id

//    let start clt = 
//        startDecider clt
//        startWorkers clt

    static let validateChildWorkflow (child : IWorkflow) =
        if child.ExecutionStartToCloseTimeout.IsNone then failwith "Child workflows must specify execution timeout"
        if child.TaskStartToCloseTimeout.IsNone then failwith "Child workflows must specify decision task timeout"
        if child.ChildPolicy.IsNone then failwith "Child workflows must specify child policy"    

    member private this.Append (toStageAction : 'a -> StageAction, args : 'a) = 
        let id = stages.Length
        let stage = { StageNumber = id; Action = toStageAction args }
        Workflow(domain, name, description, version, taskList.Name, 
                 stage :: stages,
                 ?taskStartToCloseTimeout = taskStartToCloseTimeout,
                 ?execStartToCloseTimeout = execStartToCloseTimeout,
                 ?childPolicy             = childPolicy,
                 ?identity                = identity,
                 maxAttempts              = maxAttempts, 
                 input                    = defaultArg input String.Empty)

    // #region Public Events

    [<CLIEvent>] member this.OnDecisionTaskError = onDecisionTaskError.Publish
    [<CLIEvent>] member this.OnActivityTaskError = onActivityTaskError.Publish
    [<CLIEvent>] member this.OnActivityFailed    = onActivityFailed.Publish
    [<CLIEvent>] member this.OnWorkflowFailed    = onWorkflowFailed.Publish
    [<CLIEvent>] member this.OnWorkflowCompleted = onWorkflowCompleted.Publish

    // #endregion
         
    interface IWorkflow with
        member this.Name                         = name
        member this.Description                  = description
        member this.MaxAttempts                  = maxAttempts
        member this.Version                      = version
        member this.TaskList                     = taskList
        member this.TaskStartToCloseTimeout      = taskStartToCloseTimeout
        member this.ExecutionStartToCloseTimeout = execStartToCloseTimeout
        member this.ChildPolicy                  = childPolicy
        member this.Input                        = input
        member this.Start swfClt id              = start swfClt id
        //member this.StartDecider swfClt          = startDecider swfClt
        //member this.StartWorkers swfClt          = startWorkers swfClt
                
    member this.NumberOfStages                   = stages.Length

    // #region Operators

    static member (++>) (workflow : Workflow, activity : IActivity) = workflow.Append(ScheduleActivity, activity)
    static member (++>) (workflow : Workflow, childWorkflow : IWorkflow) =         
        validateChildWorkflow childWorkflow
        workflow.Append(StartChildWorkflow, childWorkflow)
    static member (++>) (workflow : Workflow, (schedulables, _ as parallelActions) : ISchedulable[] * Reducer) =
        let childWorkflows = getWorkflows schedulables |> Array.iter validateChildWorkflow
        workflow.Append(ParallelActions, parallelActions)

    // #endregion