﻿using System;
using Microsoft.FSharp.Core;

namespace Amazon.SimpleWorkflow.Extensions.Builders
{
    internal sealed class ActivityBuilderImpl<TInput, TOutput> : IActivityBuilder<TInput, TOutput>
    {
        public ActivityBuilderImpl(
            string name,
            Func<TInput, TOutput> processor,
            int taskHeartbeatTimeout,
            int taskScheduleToStartTimeout,
            int taskStartToCloseTimeout,
            int taskScheduleToCloseTimeout)
        {
            Name = name;
            Processor = processor;
            TaskHeartbeatTimeout = taskHeartbeatTimeout;
            TaskScheduleToStartTimeout = taskScheduleToStartTimeout;
            TaskStartToCloseTimeout = taskStartToCloseTimeout;
            TaskScheduleToCloseTimeout = taskScheduleToCloseTimeout;
        }

        public ActivityBuilderImpl(
            string name,
            int taskHeartbeatTimeout,
            int taskScheduleToStartTimeout,
            int taskStartToCloseTimeout,
            int taskScheduleToCloseTimeout)
        {
            Name = name;
            TaskHeartbeatTimeout = taskHeartbeatTimeout;
            TaskScheduleToStartTimeout = taskScheduleToStartTimeout;
            TaskStartToCloseTimeout = taskStartToCloseTimeout;
            TaskScheduleToCloseTimeout = taskScheduleToCloseTimeout;
        }

        public string Name { get; private set; }

        public string Description { get; private set; }

        public string Version { get; private set; }

        public string TaskList { get; private set; }

        public Func<TInput, TOutput> Processor { get; private set; }

        public int TaskHeartbeatTimeout { get; private set; }

        public int TaskScheduleToStartTimeout { get; private set; }

        public int TaskStartToCloseTimeout { get; private set; }

        public int TaskScheduleToCloseTimeout { get; private set; }

        public int? MaxAttempts { get; private set; }

        public string Input { get; private set; }

        public IActivityBuilder<TInput, TOutput> WithVersion(string version)
        {
            Version = version;
            return this;
        }

        public IActivityBuilder<TInput, TOutput> WithDescription(string description)
        {
            Description = description;
            return this;
        }

        public IActivityBuilder<TInput, TOutput> WithTaskList(string taskList)
        {
            TaskList = taskList;
            return this;
        }

        public IActivityBuilder<TInput, TOutput> WithProcessor(Func<TInput, TOutput> processor)
        {
            Processor = processor;
            return this;
        }

        public IActivityBuilder<TInput, TOutput> WithMaxAttempts(int maxAttempts)
        {
            MaxAttempts = maxAttempts;
            return this;
        }

        public IActivityBuilder<TInput, TOutput> WithInput(string input)
        {
            Input = input;
            return this;
        }

        public IActivity Complete()
        {
            return new Activity<TInput, TOutput>(
                Name,
                Description,
                TaskHeartbeatTimeout,
                TaskScheduleToStartTimeout,
                TaskStartToCloseTimeout,
                TaskScheduleToCloseTimeout,
                Version.AsOption(string.IsNullOrWhiteSpace),
                TaskList.AsOption(string.IsNullOrWhiteSpace),
                MaxAttempts.AsOption(),
                Input.AsOption(string.IsNullOrWhiteSpace),
                Processor != null 
                    ? FuncConvert.ToFSharpFunc(new Converter<TInput, TOutput>(Processor)).AsOption()
                    : FSharpOption<FSharpFunc<TInput, TOutput>>.None
                );
        }
    }
}