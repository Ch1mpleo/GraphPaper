using GraphPaper.Application.DTOs.UserDTO;
using GraphPaper.Application.Interfaces;
using GraphPaper.Application.Utils;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace GraphPaper.API.Controllers
{
    [Route("api/auth")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IConfiguration _configuration;

        public AuthController(IAuthService authService, IConfiguration configuration)
        {
            _authService = authService;
            _configuration = configuration;
        }

        /// <summary>
        /// Register a new user account.
        /// </summary>
        [HttpPost("register")]
        [SwaggerOperation(
            Summary = "Register a new user",
            Description = "Creates a new user account with the provided registration information."
        )]
        [ProducesResponseType(typeof(ApiResult<UserDto>), 201)]
        [ProducesResponseType(typeof(ApiResult<UserDto>), 400)]
        public async Task<IActionResult> Register([FromBody] UserRegistrationDto userDto)
        {
            var result = await _authService.RegisterUserAsync(userDto);
            return StatusCode(201, ApiResult<UserDto>.Success(result!, "201", "Registered successfully."));
        }

        /// <summary>
        /// User login.
        /// </summary>
        [HttpPost("login")]
        [SwaggerOperation(
            Summary = "User login",
            Description = "Authenticate user and return JWT tokens."
        )]
        [ProducesResponseType(typeof(ApiResult<LoginResponseDto>), 200)]
        [ProducesResponseType(typeof(ApiResult<LoginResponseDto>), 400)]
        public async Task<IActionResult> Login([FromBody] LoginRequestDto dto)
        {
            var result = await _authService.LoginAsync(dto, _configuration);
            return Ok(ApiResult<LoginResponseDto>.Success(result!, "200", "Login successful."));
        }
    }
}
