using Microsoft.AspNetCore.Authorization;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;
using System;

namespace SmartcityBackend.src.Services
{
    public class TokenService : ITokenService
    {
        public async Task<TokenAuthorizationResult> IsAuthorizedAsync(
            string token,
            Guid taskId,
            Guid action)
        {
            // TODO: Add your actual token validation and authorization logic here.

            await Task.CompletedTask;

            return new TokenAuthorizationResult
            {
                Success = false,
                Message = "Authorization logic is not implemented.",
                Data = null
            };
        }

        public Guid? GetUserId(object data)
        {
            // TODO: Extract User ID from token data.

            return null;
        }
    }
}