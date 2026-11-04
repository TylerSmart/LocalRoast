using App.API.Http;
using App.Core.System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("version")]
public sealed class VersionController : ControllerBase
{
    [HttpGet]
    [HttpHead]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApplicationVersionDto), StatusCodes.Status200OK)]
    public IActionResult GetVersion()
    {
        return Ok(ApiBuildVersion.Info);
    }
}
