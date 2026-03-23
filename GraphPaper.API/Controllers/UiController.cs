using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GraphPaper.API.Controllers;

[ApiExplorerSettings(IgnoreApi = true)]
[AllowAnonymous]
[Route("ui")]
public sealed class UiController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return Redirect("/ui/auth.html");
    }

    [HttpGet("auth")]
    public IActionResult Auth()
    {
        return Redirect("/ui/auth.html");
    }

    [HttpGet("upload")]
    public IActionResult Upload()
    {
        return Redirect("/ui/upload.html");
    }

    [HttpGet("documents")]
    public IActionResult Documents()
    {
        return Redirect("/ui/documents.html");
    }

    [HttpGet("mindmap")]
    public IActionResult Mindmap()
    {
        return Redirect("/ui/mindmap.html");
    }
}
