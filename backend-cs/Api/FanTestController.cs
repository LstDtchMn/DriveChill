using Microsoft.AspNetCore.Mvc;
using DriveChill.Hardware;
using DriveChill.Models;
using DriveChill.Services;

namespace DriveChill.Api;

[ApiController]
[Route("api/fans/{fanId}/test")]
public sealed class FanTestController : ControllerBase
{
    private readonly FanTestService   _fanTest;
    private readonly IHardwareBackend _hw;

    public FanTestController(FanTestService fanTest, IHardwareBackend hw)
    {
        _fanTest = fanTest;
        _hw      = hw;
    }

    /// <summary>
    /// POST /api/fans/{fanId}/test — start a benchmark sweep.
    /// Body (optional): FanTestOptions JSON. Uses defaults if body is empty or omitted.
    /// Returns 202 Accepted with estimated_duration_s.
    /// Returns 409 Conflict if a test is already running for this fan.
    /// </summary>
    [HttpPost]
    public IActionResult StartTest(string fanId, [FromBody] FanTestOptions? options)
    {
        if (!_hw.GetFanIds().Contains(fanId))
            return NotFound(new { detail = $"Fan '{fanId}' not found" });

        options ??= new FanTestOptions();

        if (options.Steps is < 2 or > 20)
            return BadRequest(new { detail = "steps must be between 2 and 20" });

        if (options.SettleMs is < 500 or > 10_000)
            return BadRequest(new { detail = "settle_ms must be between 500 and 10000" });

        if (!_fanTest.TryStart(fanId, options, out var error))
            return Conflict(new { detail = error });

        var estimatedSeconds = (options.Steps + 1) * (options.SettleMs / 1000.0);
        return Accepted(new
        {
            ok                  = true,
            fan_id              = fanId,
            estimated_duration_s = estimatedSeconds,
        });
    }

    /// <summary>
    /// GET /api/fans/{fanId}/test — get the current or most recent result.
    /// Returns 404 if no test has run for this fan in the current session.
    /// </summary>
    [HttpGet]
    public IActionResult GetTest(string fanId)
    {
        var result = _fanTest.GetResult(fanId);
        return result == null
            ? NotFound(new { detail = $"No test result for fan '{fanId}'" })
            : Ok(result);
    }

    /// <summary>
    /// DELETE /api/fans/{fanId}/test — cancel the running test.
    /// Returns 404 if no test is currently running.
    /// </summary>
    [HttpDelete]
    public IActionResult CancelTest(string fanId)
    {
        return _fanTest.Cancel(fanId)
            ? Ok(new { ok = true })
            : NotFound(new { detail = $"No running test for fan '{fanId}'" });
    }
}
