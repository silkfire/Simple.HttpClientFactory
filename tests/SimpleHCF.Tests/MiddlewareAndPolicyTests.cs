﻿namespace SimpleHCF.Tests
{
    using FakeItEasy;
    using MessageHandlers;

    using Polly;
    using Polly.Timeout;
    using WireMock.RequestBuilders;
    using WireMock.ResponseBuilders;
    using WireMock.Server;
    using Xunit;

    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;

    public class MiddlewareAndPolicyTests
    {
        private const string _endpointUri = "/hello/world";
        private const string _endpointUriTimeout = "/timeout";

        private readonly WireMockServer _server;

        public MiddlewareAndPolicyTests()
        {
            _server = WireMockServer.Start();

            _server
                .Given(Request.Create()
                    .WithPath(_endpointUri)
                    .UsingGet())
                .InScenario("Timeout-then-resolved")
                .WillSetStateTo("Transient issue resolved")
                .RespondWith(Response.Create()
                    .WithStatusCode(HttpStatusCode.RequestTimeout));

            _server
                .Given(Request.Create()
                    .WithPath(_endpointUri)
                    .UsingGet())
                .InScenario("Timeout-then-resolved")
                .WhenStateIs("Transient issue resolved")
                .WillSetStateTo("All ok")
                .RespondWith(Response.Create()
                    .WithStatusCode(HttpStatusCode.OK)
                    .WithHeader("Content-Type", "text/plain")
                    .WithBody("Hello world!"));

            _server
                .Given(Request.Create()
                    .WithPath(_endpointUriTimeout)
                    .UsingGet())
                .RespondWith(Response.Create()
                    .WithStatusCode(HttpStatusCode.RequestTimeout));
        }

        [Fact]
        public async Task Client_with_retry_and_timeout_policy_should_properly_apply_policies_with_single_middleware()
        {
            var eventMessageHandler = new EventMessageHandler(A.Dummy<IList<string>>());

            //timeout after 2 seconds, then retry
            var clientWithRetry = HttpClientFactoryBuilder.Create()
                                                          .WithPolicy(
                                                                      Policy<HttpResponseMessage>
                                                                          .Handle<HttpRequestException>()
                                                                          .OrResult(result => result.StatusCode >= HttpStatusCode.InternalServerError || result.StatusCode == HttpStatusCode.RequestTimeout)
                                                                          .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(1)))
                                                          .WithPolicy(
                                                                      Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(4), TimeoutStrategy.Optimistic))
                                                          .WithMessageHandler(eventMessageHandler)
                                                          .Build()
                                                          .CreateClient();

            Task<HttpResponseMessage> responseTask = null;

            await Assert.RaisesAsync<EventMessageHandler.RequestEventArgs>(
                h => eventMessageHandler.Request += h,
                h => eventMessageHandler.Request -= h,
                () => responseTask = clientWithRetry.GetAsync($"{_server.Urls[0]}{_endpointUriTimeout}"));

            var responseWithTimeout = await responseTask;

            Assert.Equal(4, _server.LogEntries.Count());
            Assert.Equal(HttpStatusCode.RequestTimeout, responseWithTimeout.StatusCode);
        }


        [Fact]
        public async Task Client_with_retry_and_timeout_policy_should_properly_apply_policies_with_multiple_middlewares()
        {
            var visitedMiddleware = new List<string>();

            var trafficRecorderMessageHandler = new TrafficRecorderMessageHandler(visitedMiddleware);
            var eventMessageHandler = new EventMessageHandler(visitedMiddleware);

            //timeout after 2 seconds, then retry
            var clientWithRetry = HttpClientFactoryBuilder.Create()
                                                          .WithPolicy(
                                                                      Policy<HttpResponseMessage>
                                                                          .Handle<HttpRequestException>()
                                                                          .OrResult(result => result.StatusCode >= HttpStatusCode.InternalServerError || result.StatusCode == HttpStatusCode.RequestTimeout)
                                                                          .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(1)))
                                                          .WithPolicy(
                                                                      Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(4), TimeoutStrategy.Optimistic))
                                                          .WithMessageHandler(eventMessageHandler)
                                                          .WithMessageHandler(trafficRecorderMessageHandler)
                                                          .Build()
                                                          .CreateClient();

            Task<HttpResponseMessage> responseTask = null;

            var raisedEvent = await Assert.RaisesAsync<EventMessageHandler.RequestEventArgs>(
                h => eventMessageHandler.Request += h,
                h => eventMessageHandler.Request -= h,
                () => responseTask = clientWithRetry.GetAsync($"{_server.Urls[0]}{_endpointUriTimeout}"));

            var responseWithTimeout = await responseTask;

            Assert.True(raisedEvent.Arguments.Request.Headers.Contains("foobar"));
            Assert.Equal("foobar", raisedEvent.Arguments.Request.Headers.GetValues("foobar").FirstOrDefault());
            Assert.Single(trafficRecorderMessageHandler.Traffic);
            Assert.Equal(4, _server.LogEntries.Count());
            Assert.Equal(HttpStatusCode.RequestTimeout, responseWithTimeout.StatusCode);
        }


        [Fact]
        public async Task Retry_policy_should_work_with_multiple_middleware()
        {
            var visitedMiddleware = new List<string>();

            var trafficRecorderMessageHandler = new TrafficRecorderMessageHandler(visitedMiddleware);
            var eventMessageHandler = new EventMessageHandler(visitedMiddleware);

            var clientWithRetry = HttpClientFactoryBuilder.Create()
                                                          .WithPolicy(
                                                                      Policy<HttpResponseMessage>
                                                                          .Handle<HttpRequestException>()
                                                                          .OrResult(result => result.StatusCode >= HttpStatusCode.InternalServerError || result.StatusCode == HttpStatusCode.RequestTimeout)
                                                                          .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(1)))
                                                          .WithMessageHandler(eventMessageHandler)
                                                          .WithMessageHandler(trafficRecorderMessageHandler)
                                                          .Build()
                                                          .CreateClient();

            Task<HttpResponseMessage> responseTask = null;

            var raisedEvent = await Assert.RaisesAsync<EventMessageHandler.RequestEventArgs>(
                h => eventMessageHandler.Request += h,
                h => eventMessageHandler.Request -= h,
                () => responseTask = clientWithRetry.GetAsync($"{_server.Urls[0]}{_endpointUri}"));

            var response = await responseTask;

            Assert.True(raisedEvent.Arguments.Request.Headers.Contains("foobar"));
            Assert.Equal("foobar", raisedEvent.Arguments.Request.Headers.GetValues("foobar").FirstOrDefault());
            Assert.Single(trafficRecorderMessageHandler.Traffic);

            Assert.Equal(2, _server.LogEntries.Count());
            Assert.Single(_server.LogEntries, le => (HttpStatusCode)le.ResponseMessage.StatusCode == HttpStatusCode.OK);
            Assert.Single(_server.LogEntries, le => (HttpStatusCode)le.ResponseMessage.StatusCode == HttpStatusCode.RequestTimeout);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Hello world!", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Retry_policy_should_work_with_single_middleware()
        {
            var eventMessageHandler = new EventMessageHandler(A.Dummy<IList<string>>());
            var clientWithRetry = HttpClientFactoryBuilder.Create()
                                                          .WithPolicy(
                                                                      Policy<HttpResponseMessage>
                                                                          .Handle<HttpRequestException>()
                                                                          .OrResult(result => result.StatusCode >= HttpStatusCode.InternalServerError || result.StatusCode == HttpStatusCode.RequestTimeout)
                                                                          .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(1)))
                                                          .WithMessageHandler(eventMessageHandler)
                                                          .Build()
                                                          .CreateClient();

            Task<HttpResponseMessage> responseTask = null;

            await Assert.RaisesAsync<EventMessageHandler.RequestEventArgs>(
                h => eventMessageHandler.Request += h,
                h => eventMessageHandler.Request -= h,
                () => responseTask = clientWithRetry.GetAsync($"{_server.Urls[0]}{_endpointUri}"));

            var response = await responseTask;

            Assert.Equal(2, _server.LogEntries.Count());
            Assert.Single(_server.LogEntries, le => (HttpStatusCode)le.ResponseMessage.StatusCode == HttpStatusCode.OK);
            Assert.Single(_server.LogEntries, le => (HttpStatusCode)le.ResponseMessage.StatusCode == HttpStatusCode.RequestTimeout);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Hello world!", await response.Content.ReadAsStringAsync());
        }
    }
}
