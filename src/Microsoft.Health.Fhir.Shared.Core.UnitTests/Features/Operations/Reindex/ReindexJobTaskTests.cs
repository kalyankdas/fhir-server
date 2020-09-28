﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.Reindex;
using Microsoft.Health.Fhir.Core.Features.Operations.Reindex.Models;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Messages.Reindex;
using Microsoft.Health.Fhir.Core.UnitTests.Extensions;
using Microsoft.Health.Fhir.Core.UnitTests.Features.Search;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Operations.Reindex
{
    public class ReindexJobTaskTests
    {
        private const string PatientFileName = "Patient.ndjson";
        private const string ObservationFileName = "Observation.ndjson";
        private static readonly WeakETag _weakETag = WeakETag.FromVersionId("0");

        private readonly IFhirOperationDataStore _fhirOperationDataStore = Substitute.For<IFhirOperationDataStore>();
        private readonly ReindexJobConfiguration _reindexJobConfiguration = new ReindexJobConfiguration();
        private readonly ISearchService _searchService = Substitute.For<ISearchService>();
        private readonly IReindexUtilities _reindexUtilities = Substitute.For<IReindexUtilities>();
        private readonly IMediator _mediator = Substitute.For<IMediator>();

        private ReindexJobTask _reindexJobTask;

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly CancellationToken _cancellationToken;

        public ReindexJobTaskTests()
        {
            _cancellationToken = _cancellationTokenSource.Token;

            var job = new ReindexJobRecord("hash", 1, null);

            _fhirOperationDataStore.UpdateReindexJobAsync(job, _weakETag, _cancellationToken).ReturnsForAnyArgs(new ReindexJobWrapper(job, _weakETag));

            _searchService.SearchForReindexAsync(
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<string>(),
                true,
                Arg.Any<CancellationToken>()).
                Returns(new SearchResult(5, new List<Tuple<string, string>>()));

            _reindexJobTask = new ReindexJobTask(
                () => _fhirOperationDataStore.CreateMockScope(),
                Options.Create(_reindexJobConfiguration),
                () => _searchService.CreateMockScope(),
                SearchParameterFixtureData.SupportedSearchDefinitionManager,
                _reindexUtilities,
                _mediator,
                NullLogger<ReindexJobTask>.Instance);

            _mediator.Send(Arg.Any<ReindexJobCompletedRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ReindexJobCompletedResponse(true, null));
        }

        [Fact]
        public async Task GivenSupportedParams_WhenExecuted_ThenCorrectSearchIsPerformed()
        {
            // Add one parameter that needs to be indexed
            var param = SearchParameterFixtureData.SearchDefinitionManager.AllSearchParameters.Where(p => p.Name == "status").FirstOrDefault();
            param.IsSearchable = false;

            var job = new ReindexJobRecord("hash", 1, null);

            // setup search result
            _searchService.SearchForReindexAsync(
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<string>(),
                false,
                Arg.Any<CancellationToken>()).
                Returns(CreateSearchResult());

            await _reindexJobTask.ExecuteAsync(job, _weakETag, _cancellationToken);

            // verify search for count
            await _searchService.Received().SearchForReindexAsync(Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>());

            // verify search for results
            await _searchService.Received().SearchForReindexAsync(
                Arg.Is<IReadOnlyList<Tuple<string, string>>>(l => l.Where(t => t.Item1 == "_type" && t.Item2 == "Account").Any()),
                Arg.Any<string>(),
                false,
                Arg.Any<CancellationToken>());

            Assert.Equal(OperationStatus.Completed, job.Status);
            Assert.Equal(5, job.Count);
            Assert.Equal("Account", job.ResourceList);
            Assert.Equal("http://hl7.org/fhir/SearchParameter/Account-status", job.SearchParamList);
            Assert.Collection<ReindexJobQueryStatus>(
                job.QueryList,
                item => Assert.True(item.ContinuationToken == null && item.Status == OperationStatus.Completed));

            param.IsSearchable = true;
        }

        [Fact]
        public async Task GivenContinuationToken_WhenExecuted_ThenAdditionalQueryAdded()
        {
            // Add one parameter that needs to be indexed
            var param = SearchParameterFixtureData.SearchDefinitionManager.AllSearchParameters.Where(p => p.Name == "identifier").FirstOrDefault();
            param.IsSearchable = false;

            var job = new ReindexJobRecord("hash", 1, null);

            // setup search result
            _searchService.SearchForReindexAsync(
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<string>(),
                false,
                Arg.Any<CancellationToken>()).
                Returns(
                    x => CreateSearchResult("token"),
                    x => CreateSearchResult());

            await _reindexJobTask.ExecuteAsync(job, _weakETag, _cancellationToken);

            // verify search for count
            await _searchService.Received().SearchForReindexAsync(Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>());

            // verify search for results
            await _searchService.Received().SearchForReindexAsync(
                Arg.Is<IReadOnlyList<Tuple<string, string>>>(l => l.Where(t => t.Item1 == "_type" && t.Item2 == "Account").Any()),
                Arg.Any<string>(),
                false,
                Arg.Any<CancellationToken>());

            Assert.Equal(OperationStatus.Completed, job.Status);
            Assert.Equal(5, job.Count);
            Assert.Equal("Account", job.ResourceList);
            Assert.Equal("http://hl7.org/fhir/SearchParameter/Account-identifier", job.SearchParamList);
            Assert.Collection<ReindexJobQueryStatus>(
                job.QueryList,
                item => Assert.True(item.ContinuationToken == "token" && item.Status == OperationStatus.Completed),
                item2 => Assert.True(item2.ContinuationToken == null && item2.Status == OperationStatus.Completed));

            param.IsSearchable = true;
        }

        [Fact]
        public async Task GivenRunningJob_WhenExecuted_ThenQueuedQueryCompleted()
        {
            // Add one parameter that needs to be indexed
            var param = SearchParameterFixtureData.SearchDefinitionManager.AllSearchParameters.Where(p => p.Name == "appointment").FirstOrDefault();
            param.IsSearchable = false;

            var job = new ReindexJobRecord("hash", 1, null);

            // setup search result
            _searchService.SearchForReindexAsync(
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<string>(),
                false,
                Arg.Any<CancellationToken>()).
                Returns(
                    x => CreateSearchResult("token"),
                    x => CreateSearchResult());

            await _reindexJobTask.ExecuteAsync(job, _weakETag, _cancellationToken);

            // verify search for count
            await _searchService.Received().SearchForReindexAsync(Arg.Any<IReadOnlyList<Tuple<string, string>>>(), Arg.Any<string>(), true, Arg.Any<CancellationToken>());

            // verify search for results
            await _searchService.Received().SearchForReindexAsync(
                Arg.Is<IReadOnlyList<Tuple<string, string>>>(l => l.Where(t => t.Item1 == "_type" && t.Item2 == "Appointment,AppointmentResponse").Any()),
                Arg.Is<string>("hash"),
                false,
                Arg.Any<CancellationToken>());

            // verify search for results with token
            await _searchService.Received().SearchForReindexAsync(
                Arg.Is<IReadOnlyList<Tuple<string, string>>>(
                    l => l.Where(t => t.Item1 == "_type" && t.Item2 == "Appointment,AppointmentResponse").Any() &&
                    l.Where(t => t.Item1 == KnownQueryParameterNames.ContinuationToken && t.Item2 == "token").Any()),
                Arg.Is<string>("hash"),
                false,
                Arg.Any<CancellationToken>());

            await _reindexJobTask.ExecuteAsync(job, _weakETag, _cancellationToken);

            Assert.Equal(OperationStatus.Completed, job.Status);
            Assert.Equal(5, job.Count);
            Assert.Equal("Appointment,AppointmentResponse", job.ResourceList);
            Assert.Equal("http://hl7.org/fhir/SearchParameter/AppointmentResponse-appointment", job.SearchParamList);
            Assert.Collection<ReindexJobQueryStatus>(
                job.QueryList,
                item => Assert.True(item.ContinuationToken == "token" && item.Status == OperationStatus.Completed),
                item2 => Assert.True(item2.ContinuationToken == null && item2.Status == OperationStatus.Completed));

            await _mediator.Received().Send(
                Arg.Is<ReindexJobCompletedRequest>(r => r.SearchParameterUris.Where(s => s.Contains("Appointment")).Any() &&
                                            r.SearchParameterUris.Where(s => s.Contains("AppointmentResponse")).Any()),
                Arg.Any<CancellationToken>());

            param.IsSearchable = true;
        }

        [Fact]
        public async Task GivenNoSupportedParams_WhenExecuted_ThenJobCanceled()
        {
            var job = new ReindexJobRecord("hash", 1, null);

            await _reindexJobTask.ExecuteAsync(job, _weakETag, _cancellationToken);

            Assert.Equal(OperationStatus.Canceled, job.Status);
            await _searchService.DidNotReceiveWithAnyArgs().SearchForReindexAsync(default, default, default, default);
        }

        [Fact]
        public async Task GivenQueryInRunningState_WhenExecuted_ThenQueryResetToQueuedOnceStale()
        {
            // Add one parameter that needs to be indexed
            var param = SearchParameterFixtureData.SearchDefinitionManager.AllSearchParameters.Where(p => p.Name == "appointment").FirstOrDefault();
            param.IsSearchable = false;

            _reindexJobConfiguration.JobHeartbeatTimeoutThreshold = new TimeSpan(0, 0, 0, 1, 0);

            var job = new ReindexJobRecord("hash", maxiumumConcurrency: 1, scope: null, 3);

            job.QueryList.Add(new ReindexJobQueryStatus("token") { Status = OperationStatus.Running });

            // setup search results
            _searchService.SearchForReindexAsync(
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<string>(),
                false,
                Arg.Any<CancellationToken>()).
                Returns(
                    x => CreateSearchResult("token1", 3),
                    x => CreateSearchResult("token2", 3),
                    x => CreateSearchResult("token3", 3),
                    x => CreateSearchResult("token4", 3),
                    x => CreateSearchResult(null, 2));

            await _reindexJobTask.ExecuteAsync(job, _weakETag, _cancellationToken);

            param.IsSearchable = true;

            Assert.Equal(OperationStatus.Completed, job.Status);
            Assert.Equal(5, job.QueryList.Count);
        }

        [Fact]
        public async Task GivenQueryWhichContinuallyFails_WhenExecuted_ThenJobWillBeMarkedFailed()
        {
            // Add one parameter that needs to be indexed
            var param = SearchParameterFixtureData.SearchDefinitionManager.AllSearchParameters.Where(p => p.Name == "appointment").FirstOrDefault();
            param.IsSearchable = false;

            var job = new ReindexJobRecord("hash", maxiumumConcurrency: 1, scope: null, 3);

            job.QueryList.Add(new ReindexJobQueryStatus("token") { Status = OperationStatus.Running });

            // setup search results
            _searchService.SearchForReindexAsync(
                Arg.Any<IReadOnlyList<Tuple<string, string>>>(),
                Arg.Any<string>(),
                false,
                Arg.Any<CancellationToken>()).
                Returns(CreateSearchResult(null, 2));

            _reindexUtilities.ProcessSearchResultsAsync(Arg.Any<SearchResult>(), "hash", Arg.Any<CancellationToken>())
                .Throws(new Exception("Failed to process query"));

            await _reindexJobTask.ExecuteAsync(job, _weakETag, _cancellationToken);

            param.IsSearchable = true;

            Assert.Equal(_reindexJobConfiguration.ConsecutiveFailuresThreshold, job.QueryList.First().FailureCount);
            Assert.Equal(OperationStatus.Failed, job.Status);
        }

        private SearchResult CreateSearchResult(string continuationToken = null, int resourceCount = 1)
        {
            var resultList = new List<SearchResultEntry>();

            for (var i = 0; i < resourceCount; i++)
            {
                var wrapper = Substitute.For<ResourceWrapper>();
                var entry = new SearchResultEntry(wrapper);
                resultList.Add(entry);
            }

            return new SearchResult(resultList, new List<Tuple<string, string>>(), new List<(string, string)>(), continuationToken);
        }
    }
}
