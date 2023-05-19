// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Models.ServiceBus;
using Hosts.GraphUpdater;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json;
using Repositories.Mocks;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Models;

namespace Services.Tests
{
    [TestClass]
    public class StarterFunctionTests
    {
        private string _instanceId;
        private MockLoggingRepository _loggerMock;
        private Mock<IDurableOrchestrationClient> _durableClientMock;
        private SyncJob _syncJob;

        [TestInitialize]
        public void SetupTest()
        {
            _instanceId = "1234567890";
            _durableClientMock = new Mock<IDurableOrchestrationClient>();
            _loggerMock = new MockLoggingRepository();
            _syncJob = new SyncJob
            {
                PartitionKey = "00-00-0000",
                RowKey = Guid.NewGuid().ToString(),
                TargetOfficeGroupId = Guid.NewGuid(),
                ThresholdPercentageForAdditions = 80,
                ThresholdPercentageForRemovals = 20,
                LastRunTime = DateTime.UtcNow.AddDays(-1),
                Requestor = "user@domail.com",
                RunId = Guid.NewGuid(),
                ThresholdViolations = 0
            };
        }

        [TestMethod]
        public async Task ProcessValidRequestTest()
        {
            _durableClientMock
                .Setup(x => x.StartNewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MembershipHttpRequest>()))
                .ReturnsAsync(_instanceId);

            var starterFunction = new StarterFunction(_loggerMock);
            var groupMembership = GetGroupMembership();
            var content = new MembershipHttpRequest
            {
                FilePath = "file/path/name.json",
                SyncJob = _syncJob
            };

            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                Content = new StringContent(JsonConvert.SerializeObject(content)),
            };

            var response = await starterFunction.RunAsync(request, _durableClientMock.Object);

            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            Assert.IsNotNull(_loggerMock.MessagesLogged.Single(x => x.Message.Contains("function started")));
            Assert.IsNotNull(_loggerMock.MessagesLogged.Single(x => x.Message.Contains("InstanceId:")));
            Assert.IsNotNull(_loggerMock.MessagesLogged.Single(x => x.Message.Contains("function complete")));
        }

        [TestMethod]
        public async Task ProcessEmptyRequestTest()
        {
            _durableClientMock
                  .Setup(x => x.StartNewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MembershipHttpRequest>()))
                  .ReturnsAsync(_instanceId);

            var starterFunction = new StarterFunction(_loggerMock);
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post
            };

            var response = await starterFunction.RunAsync(request, _durableClientMock.Object);
            var responseContent = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("Request content is empty.", responseContent);
            Assert.AreEqual(0, _loggerMock.MessagesLogged.Count(x => x.Message.Contains("function started")));
            Assert.AreEqual(0, _loggerMock.MessagesLogged.Count(x => x.Message.Contains("function complete")));
        }

        [TestMethod]
        public async Task ProcessInvalidRequestTest()
        {
            _durableClientMock
                  .Setup(x => x.StartNewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MembershipHttpRequest>()))
                  .ReturnsAsync(_instanceId);

            var starterFunction = new StarterFunction(_loggerMock);
            var groupMembership = GetGroupMembership();
            var content = new MembershipHttpRequest
            {
                FilePath = null,
                SyncJob = _syncJob
            };

            var request = new HttpRequestMessage
            {
                Content = new StringContent(JsonConvert.SerializeObject(content)),
            };

            var response = await starterFunction.RunAsync(request, _durableClientMock.Object);
            var responseContent = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("Request is not valid, FilePath is missing.", responseContent);
            Assert.AreEqual(0, _loggerMock.MessagesLogged.Count(x => x.Message.Contains("function started")));
            Assert.AreEqual(0, _loggerMock.MessagesLogged.Count(x => x.Message.Contains("function complete")));
        }

        private string GetMembershipBody(int totalMessageCount = 1)
        {
            var json =
            "{" +
            "  'Sources': [" +
            "    {" +
            "      'ObjectId': '8032abf6-b4b1-45b1-8e7e-40b0bd16d6eb'" +
            "    }" +
            "  ]," +
            "  'Destination': {" +
            "    'ObjectId': 'dc04c21f-091a-44a9-a661-9211dd9ccf35'" +
            "  }," +
            "  'SourceMembers': []," +
            "  'RunId': '501f6c70-8fe1-496f-8446-befb15b5249a'," +
            "  'SyncJobRowKey': '0a4cc250-69a0-4019-8298-96bf492aca01'," +
            "  'SyncJobPartitionKey': '2021-01-01'," +
            "  'Errored': false," +
            "  'TotalMessageCount': " + totalMessageCount.ToString() +
            "}";

            return json;
        }

        private GroupMembership GetGroupMembership(int totalMessageCount = 1)
        {
            var json = GetMembershipBody(totalMessageCount);
            var groupMembership = JsonConvert.DeserializeObject<GroupMembership>(json);

            return groupMembership;
        }
    }
}
