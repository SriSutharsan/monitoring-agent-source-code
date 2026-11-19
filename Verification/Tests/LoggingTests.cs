using Agent.Logging;

namespace Agent.Verify;

public static class LoggingTests
{
    public static void Register(TestRunner t)
    {
        t.Add("Redactor: bearer tokens, passwords, registration tokens and JWTs are masked", () =>
        {
            var s = SecretRedactor.Redact("POST failed Authorization: Bearer abcdef1234567890SECRET");
            Assert.True(!s.Contains("abcdef1234567890SECRET"), s);
            s = SecretRedactor.Redact("{\"registrationToken\":\"tok_live_9f8e7d6c\",\"hostname\":\"LAPTOP-1\"}");
            Assert.True(!s.Contains("tok_live_9f8e7d6c") && s.Contains("LAPTOP-1"), s);
            s = SecretRedactor.Redact("url=https://x/api?apikey=ZZZ999&b=1");
            Assert.True(!s.Contains("ZZZ999") && s.Contains("b=1"), s);
            s = SecretRedactor.Redact("jwt eyJhbGciOiJIUzI1NiJ9abcdefghij.eyJzdWIiOiIxMjM0NTY3ODkwIn0abcdef.SflKxwRJSMeKKF2QT4fw");
            Assert.True(!s.Contains("eyJzdWIi"), s);
        });

        t.Add("Redactor: ordinary text is untouched", () =>
        {
            var text = "Collector perf.cpu_memory finished in 120 ms for device 4c4c4544";
            Assert.Equal(text, SecretRedactor.Redact(text));
            Assert.Equal((string)null, SecretRedactor.Redact(null));
        });
    }
}
