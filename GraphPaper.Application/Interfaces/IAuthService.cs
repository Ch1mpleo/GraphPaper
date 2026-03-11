using GraphPaper.Application.DTOs.UserDTO;
using Microsoft.Extensions.Configuration;

namespace GraphPaper.Application.Interfaces
{
    public interface IAuthService
    {
        Task<UserDto?> RegisterUserAsync(UserRegistrationDto registrationDto);

        Task<LoginResponseDto?> LoginAsync(LoginRequestDto loginDto, IConfiguration configuration);

    }
}