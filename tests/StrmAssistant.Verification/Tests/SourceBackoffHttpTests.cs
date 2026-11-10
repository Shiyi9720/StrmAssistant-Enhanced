using StrmAssistant.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Drives the real SourceBackoff through status codes produced by an injected
    /// HttpMessageHandler (no network). The small wrapper below mirrors the policy of the two
    /// production call sites (DoubanApi.GetJsonAsync: 403/418/429; IntroDbApi: 429/5xx), which
    /// cannot be linked here because they pull in Emby's IHttpClient/ProxyApi. The backoff state
    /// machine, ladder and short-circuit are the real compiled SourceBackoff.
    ///
    /// SourceBackoff has no clock injection, so between ladder steps the test rewinds the
    /// in-memory expiry with reflection (test-only; production code and behaviour untouched).
    /// </summary>
    internal static class SourceBackoffHttpTests
    {
        private const double ToleranceSeconds = 30;

        public static void Run(TestRunner runner)
        {
            LadderViaStatusCodes(runner);
            ShortCircuitWhileBackingOff(runner);
            SuccessClearsAndRestarts(runner);
            KeysAreIndependent(runner);
        }

        private static void LadderViaStatusCodes(TestRunner runner)
        {
            runner.Section("E1. 403 -> 429 -> 429 advances 10m -> 30m -> 2h");

            var key = NewKey("http-ladder");

            var handler = new SequenceHandler(new[]
            {
                HttpStatusCode.Forbidden,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.TooManyRequests
            });

            var probe = new BackoffProbe(handler, key);

            var first = probe.CallAsync().GetAwaiter().GetResult();
            runner.EqualLong("E1 first request returned 403", 403, (int) first.Value);
            runner.EqualLong("E1 handler saw one request", 1, handler.Requests);
            CheckWindow(runner, "E1 after 1st failure (403) waits 10m",
                SourceBackoff.UnavailableUntil(key), TimeSpan.FromMinutes(10));

            Expire(key);

            var second = probe.CallAsync().GetAwaiter().GetResult();
            runner.EqualLong("E1 second request returned 429", 429, (int) second.Value);
            runner.EqualLong("E1 handler saw two requests", 2, handler.Requests);
            CheckWindow(runner, "E1 after 2nd failure (429) waits 30m",
                SourceBackoff.UnavailableUntil(key), TimeSpan.FromMinutes(30));

            Expire(key);

            var third = probe.CallAsync().GetAwaiter().GetResult();
            runner.EqualLong("E1 third request returned 429", 429, (int) third.Value);
            runner.EqualLong("E1 handler saw three requests", 3, handler.Requests);
            CheckWindow(runner, "E1 after 3rd failure (429) waits 2h",
                SourceBackoff.UnavailableUntil(key), TimeSpan.FromHours(2));
        }

        private static void ShortCircuitWhileBackingOff(TestRunner runner)
        {
            runner.Section("E2. an active backoff short-circuits before the request is sent");

            var key = NewKey("http-short-circuit");
            var handler = new SequenceHandler(new[] {HttpStatusCode.Forbidden});
            var probe = new BackoffProbe(handler, key);

            probe.CallAsync().GetAwaiter().GetResult();

            runner.EqualLong("E2 first request was sent", 1, handler.Requests);
            runner.Check("E2 backoff is active", SourceBackoff.ShouldSkip(key));

            for (var i = 0; i < 3; i++)
            {
                var result = probe.CallAsync().GetAwaiter().GetResult();

                runner.Check("E2 call " + (i + 1) + " is short-circuited", result is null,
                    result is null ? null : "returned " + (int) result.Value);
            }

            runner.EqualLong("E2 handler still saw exactly one request", 1, handler.Requests);
            runner.EqualLong("E2 three calls were short-circuited", 3, probe.ShortCircuited);
            CheckWindow(runner, "E2 window was not extended by short-circuited calls",
                SourceBackoff.UnavailableUntil(key), TimeSpan.FromMinutes(10));
        }

        private static void SuccessClearsAndRestarts(TestRunner runner)
        {
            runner.Section("E3. a 200 clears the backoff and the next failure restarts at 10m");

            var key = NewKey("http-recover");

            var handler = new SequenceHandler(new[]
            {
                HttpStatusCode.Forbidden,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.OK,
                HttpStatusCode.TooManyRequests
            });

            var probe = new BackoffProbe(handler, key);

            probe.CallAsync().GetAwaiter().GetResult();
            Expire(key);
            probe.CallAsync().GetAwaiter().GetResult();

            CheckWindow(runner, "E3 before recovery waits 30m",
                SourceBackoff.UnavailableUntil(key), TimeSpan.FromMinutes(30));

            Expire(key);

            var ok = probe.CallAsync().GetAwaiter().GetResult();
            runner.EqualLong("E3 third request returned 200", 200, (int) ok.Value);
            runner.Check("E3 success clears ShouldSkip", !SourceBackoff.ShouldSkip(key));
            runner.Check("E3 success clears UnavailableUntil",
                SourceBackoff.UnavailableUntil(key) is null);

            var after = probe.CallAsync().GetAwaiter().GetResult();
            runner.EqualLong("E3 next request returned 429 again", 429, (int) after.Value);
            CheckWindow(runner, "E3 failure after success restarts at 10m",
                SourceBackoff.UnavailableUntil(key), TimeSpan.FromMinutes(10));
        }

        private static void KeysAreIndependent(TestRunner runner)
        {
            runner.Section("E4. two keys do not share backoff state");

            var failingKey = NewKey("http-key-a");
            var healthyKey = NewKey("http-key-b");

            var failing = new BackoffProbe(
                new SequenceHandler(new[] {HttpStatusCode.Forbidden}), failingKey);
            var healthy = new BackoffProbe(
                new SequenceHandler(new[] {HttpStatusCode.OK, HttpStatusCode.OK}), healthyKey);

            failing.CallAsync().GetAwaiter().GetResult();
            healthy.CallAsync().GetAwaiter().GetResult();

            runner.Check("E4 failing key is backed off", SourceBackoff.ShouldSkip(failingKey));
            runner.Check("E4 healthy key is not backed off", !SourceBackoff.ShouldSkip(healthyKey));
            runner.Check("E4 healthy key has no window",
                SourceBackoff.UnavailableUntil(healthyKey) is null);

            healthy.CallAsync().GetAwaiter().GetResult();

            CheckWindow(runner, "E4 clearing the healthy key leaves the failing key at 10m",
                SourceBackoff.UnavailableUntil(failingKey), TimeSpan.FromMinutes(10));
        }

        private static void CheckWindow(TestRunner runner, string name, DateTime? until,
            TimeSpan expected)
        {
            if (!until.HasValue)
            {
                runner.Check(name, false, "UnavailableUntil was null");
                return;
            }

            var remaining = (until.Value - DateTime.UtcNow).TotalSeconds;
            var ok = remaining >= expected.TotalSeconds - ToleranceSeconds &&
                     remaining <= expected.TotalSeconds + ToleranceSeconds;

            runner.Check(name, ok,
                "expected ~" + expected.TotalSeconds.ToString("F0") + "s, actual " +
                remaining.ToString("F1") + "s");
        }

        /// <summary>
        /// Test-only clock rewind: SourceBackoff stores the expiry in a private nested State.
        /// Rewinding it lets the ladder be exercised without waiting 10 minutes.
        /// </summary>
        private static void Expire(string key)
        {
            var statesField = typeof(SourceBackoff).GetField("States",
                BindingFlags.NonPublic | BindingFlags.Static);

            var states = (IDictionary) statesField?.GetValue(null);

            if (states is null || !states.Contains(key))
            {
                throw new InvalidOperationException("No backoff state for key " + key);
            }

            var state = states[key];
            var untilField = state.GetType().GetField("UntilUtc",
                BindingFlags.Public | BindingFlags.Instance);

            untilField?.SetValue(state, DateTime.UtcNow.AddSeconds(-1));
        }

        private static string NewKey(string area)
        {
            return "verify-http-" + area + "-" + Guid.NewGuid().ToString("N");
        }

        private sealed class SequenceHandler : HttpMessageHandler
        {
            private readonly Queue<HttpStatusCode> _codes;

            public SequenceHandler(IEnumerable<HttpStatusCode> codes)
            {
                _codes = new Queue<HttpStatusCode>(codes);
            }

            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests++;
                var code = _codes.Count > 0 ? _codes.Dequeue() : HttpStatusCode.OK;

                return Task.FromResult(new HttpResponseMessage(code));
            }
        }

        /// <summary>Mirrors DoubanApi.GetJsonAsync's status-code policy on top of SourceBackoff.</summary>
        private sealed class BackoffProbe
        {
            private readonly HttpClient _client;
            private readonly string _key;

            public BackoffProbe(HttpMessageHandler handler, string key)
            {
                _client = new HttpClient(handler, false);
                _key = key;
            }

            public int ShortCircuited { get; private set; }

            public async Task<HttpStatusCode?> CallAsync()
            {
                if (SourceBackoff.ShouldSkip(_key))
                {
                    ShortCircuited++;
                    return null;
                }

                using var response = await _client.GetAsync("https://backoff.invalid/" + _key)
                    .ConfigureAwait(false);

                var status = response.StatusCode;

                if (status == HttpStatusCode.Forbidden ||
                    status == (HttpStatusCode) 418 ||
                    status == HttpStatusCode.TooManyRequests)
                {
                    SourceBackoff.ReportFailure(_key);
                    return status;
                }

                if (status != HttpStatusCode.OK) return status;

                SourceBackoff.ReportSuccess(_key);
                return status;
            }
        }
    }
}
