using System;

namespace Frontend.Services
{
    public class AuthState
    {
        public bool IsLoggedIn { get; private set; }
        public bool IsAdmin { get; private set; }
        
        public event Action? OnChange;
        
        public void SetLoginState(bool isLoggedIn, bool isAdmin = false)
        {
            if (IsLoggedIn != isLoggedIn || IsAdmin != isAdmin)
            {
                IsLoggedIn = isLoggedIn;
                IsAdmin = isAdmin;
                OnChange?.Invoke();
            }
        }
    }
}