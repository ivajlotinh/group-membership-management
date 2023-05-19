// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Models;
using Moq;
using Repositories.Contracts;
using Repositories.GraphGroups;
using WebApi.Controllers.v1.Jobs;
using WebApi.Models.Responses;

namespace Services.Tests
{
    [TestClass]
    public class JobsControllerTests
    {
        private int _jobCount = 1000;

        private List<string> _groupTypes = null!;
        private List<SyncJob> _jobEntities = null!;
        private List<AzureADGroup> _groups = null!;
        private JobsController _jobsController = null!;
        private GetJobsHandler _getJobsHandler = null!;
        private TelemetryClient _telemetryClient = null!;
        private Mock<IRequestAdapter> _requestAdapter = null!;
        private Mock<ILoggingRepository> _loggingRepository = null!;
        private Mock<ISyncJobRepository> _syncJobRepository = null!;
        private Mock<GraphServiceClient> _graphServiceClient = null!;
        private Mock<IGraphGroupRepository> _graphGroupRepository = null!;

        [TestInitialize]
        public void Initialize()
        {
            _groups = new List<AzureADGroup>();
            _requestAdapter = new Mock<IRequestAdapter>();
            _loggingRepository = new Mock<ILoggingRepository>();
            _syncJobRepository = new Mock<ISyncJobRepository>();

            _requestAdapter.SetupProperty(x => x.BaseUrl).SetReturnsDefault("https://graph.microsoft.com/v1.0");

            _graphServiceClient = new Mock<GraphServiceClient>(_requestAdapter.Object,
                                                               "https://graph.microsoft.com/v1.0");

            _graphGroupRepository = new Mock<IGraphGroupRepository>();

            _graphGroupRepository.Setup(x => x.GetGroupsAsync(It.IsAny<List<Guid>>()))
                                    .ReturnsAsync(() => _groups);

            var telemetryConfiguration = new TelemetryConfiguration();
            _telemetryClient = new TelemetryClient(telemetryConfiguration);

            _groupTypes = new List<string>
            {
                "Microsoft 365",
                "Security",
                "Mail enabled security",
                "Distribution"
            };

            _jobEntities = Enumerable.Range(0, _jobCount).Select(x => new SyncJob
            {
                PartitionKey = Guid.NewGuid().ToString(),
                RowKey = Guid.NewGuid().ToString(),
                Status = ((SyncStatus)Random.Shared.Next(1, 15)).ToString(),
                TargetOfficeGroupId = Guid.NewGuid(),
                LastSuccessfulRunTime = DateTime.UtcNow.AddHours(-4),
                LastSuccessfulStartTime = DateTime.UtcNow.AddHours(-5),
                StartDate = DateTime.UtcNow.AddMonths(-1),
                ThresholdPercentageForAdditions = 10,
                ThresholdPercentageForRemovals = 10,
                Period = 6
            }).ToList();

            _jobEntities.ForEach(x =>
            {
                _groups.Add(new AzureADGroup
                {
                    ObjectId = x.TargetOfficeGroupId,
                    Type = _groupTypes[Random.Shared.Next(0, _groupTypes.Count)]
                });
            });

            _syncJobRepository.Setup(x => x.GetSyncJobsAsync(true, SyncStatus.All))
                              .Returns(() => ToAsyncEnumerable(_jobEntities));

            _getJobsHandler = new GetJobsHandler(_loggingRepository.Object,
                                                 _syncJobRepository.Object,
                                                 _graphGroupRepository.Object);

            _jobsController = new JobsController(_getJobsHandler);
        }

        [TestMethod]
        public async Task GetJobsTestAsync()
        {
            var response = await _jobsController.GetJobsAsync();
            var result = response.Result as OkObjectResult;

            Assert.IsNotNull(response);
            Assert.IsNotNull(result);
            Assert.IsNotNull(result.Value);

            var jobs = result.Value as GetJobsModel;

            Assert.IsNotNull(jobs);
            Assert.AreEqual(_jobCount, jobs.Count);
            Assert.AreEqual(_jobCount, jobs.Select(x => x.TargetGroupId).Distinct().Count());
            Assert.IsTrue(jobs.All(x => x.RowKey != null && x.PartitionKey != null));
            Assert.IsTrue(jobs.All(x => x.EstimatedNextRunTime == x.LastSuccessfulRunTime.AddHours(6)));
            Assert.IsTrue(jobs.All(x => x.Status != null));
            Assert.IsTrue(jobs.All(x => x.TargetGroupType != null));
        }

        [TestMethod]
        public async Task GetJobsTestWithGraphAPIFailureAsync()
        {
            _requestAdapter.Setup(x => x.ConvertToNativeRequestAsync<HttpRequestMessage>(It.IsAny<RequestInformation>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(() => new HttpRequestMessage());

            _requestAdapter.Setup(x => x.SendNoContentAsync(
                                                It.IsAny<RequestInformation>(),
                                                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>>(),
                                                It.IsAny<CancellationToken>()
                                 )).ThrowsAsync(new ApiException("GraphAPI exception"));

            _graphServiceClient = new Mock<GraphServiceClient>(_requestAdapter.Object,
                                                               "https://graph.microsoft.com/v1.0");

            var graphGroupRepository = new GraphGroupRepository(
                                                _graphServiceClient.Object,
                                                _telemetryClient,
                                                _loggingRepository.Object);

            _getJobsHandler = new GetJobsHandler(
                                     _loggingRepository.Object,
                                     _syncJobRepository.Object,
                                     graphGroupRepository);

            _jobsController = new JobsController(_getJobsHandler);
            var response = await _jobsController.GetJobsAsync();
            var result = response.Result as OkObjectResult;

            Assert.IsNotNull(response);
            Assert.IsNotNull(result);
            Assert.IsNotNull(result.Value);

            var jobs = result.Value as GetJobsModel;

            Assert.IsNotNull(jobs);
            Assert.AreEqual(_jobCount, jobs.Count);
            Assert.AreEqual(_jobCount, jobs.Select(x => x.TargetGroupId).Distinct().Count());
            Assert.IsTrue(jobs.All(x => x.RowKey != null && x.PartitionKey != null));
            Assert.IsTrue(jobs.All(x => x.EstimatedNextRunTime == x.LastSuccessfulRunTime.AddHours(6)));
            Assert.IsTrue(jobs.All(x => x.Status != null));
            Assert.IsTrue(jobs.All(x => x.TargetGroupType == null));

            _loggingRepository.Verify(x => x.LogMessageAsync
            (
                It.Is<LogMessage>(x => x.Message.StartsWith("Unable to retrieve group types")),
                It.IsAny<VerbosityLevel>(),
                It.IsAny<string>(),
                It.IsAny<string>()
            ), Times.Once);
        }

        private async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> input)
        {
            foreach (var value in await Task.FromResult(input))
            {
                yield return value;
            }
        }
    }
}

