using GraphPaper.Application.DTOs.UserDTO;
using GraphPaper.Application.Interfaces;
using GraphPaper.Application.Utils;
using GraphPaper.Domain.Entities;
using GraphPaper.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GraphPaper.Application.Services
{
    public class AuthService : IAuthService
    {
        private readonly ILogger _loggerService;
        private readonly IClaimsService _claimsService;
        private readonly IUnitOfWork _unitOfWork;

        public AuthService(
            IUnitOfWork unitOfWork,
            ILogger<AuthService> loggerService,
            IClaimsService claimsService)
        {
            _unitOfWork = unitOfWork;
            _loggerService = loggerService;
            _claimsService = claimsService;
        }

        /// <summary>
        ///     Register a new user.
        /// </summary>
        /// <param name="registrationDto"></param>
        /// <returns></returns>
        public async Task<UserDto?> RegisterUserAsync(UserRegistrationDto registrationDto)
        {
            _loggerService.LogInformation($"Start registration for {registrationDto.Email}");

            if (await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Email == registrationDto.Email) != null)
            {
                _loggerService.LogWarning($"Email {registrationDto.Email} already registered.");
                throw ErrorHelper.Conflict("Email have been used.");
            }

            var hashedPassword = new PasswordHasher().HashPassword(registrationDto.Password);

            var user = new User
            {
                Email = registrationDto.Email,
                Username = registrationDto.Username,
                HashedPassword = hashedPassword,

            };

            await _unitOfWork.Users.AddAsync(user);
            await _unitOfWork.SaveChangesAsync();

            _loggerService.LogInformation($"User {user.Email} created successfully.");

            return new UserDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                CreatedAt = user.CreatedAt
            };
        }

        /// <summary>
        ///     Login a user and return JWT access and refresh token.
        /// </summary>
        /// <param name="loginDto"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public async Task<LoginResponseDto?> LoginAsync(LoginRequestDto loginDto, IConfiguration configuration)
        {
            _loggerService.LogInformation($"Login attempt for {loginDto.Email}");

            // Get user from DB
            var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Email == loginDto.Email && !u.IsDeleted);

            if (user == null)
                throw ErrorHelper.NotFound("Account does not exist.");

            if (!new PasswordHasher().VerifyPassword(loginDto.Password!, user.HashedPassword))
                throw ErrorHelper.Unauthorized("Password is incorrect.");

            if (user.IsDeleted)
                throw ErrorHelper.Forbidden("Your account has been disabled. Please contact support for more information.");

            _loggerService.LogInformation($"User {loginDto.Email} authenticated successfully.");

            // Generate JWT token
            var accessToken = JwtUtils.GenerateJwtToken(
                user.Id,
                user.Email,
                "User",
                configuration,
                TimeSpan.FromMinutes(30)
            );

            await _unitOfWork.Users.Update(user);
            await _unitOfWork.SaveChangesAsync();

            _loggerService.LogInformation($"Tokens generated and user cache updated for {user.Email}");

            return new LoginResponseDto
            {
                AccessToken = accessToken
            };
        }
    }
}