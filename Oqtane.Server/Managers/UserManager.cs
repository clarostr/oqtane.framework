using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Oqtane.Enums;
using Oqtane.Infrastructure;
using Oqtane.Models;
using Oqtane.Repository;
using Oqtane.Shared;

namespace Oqtane.Managers
{
    public class UserManager : IUserManager
    {
        private readonly IUserRepository _users;
        private readonly IRoleRepository _roles;
        private readonly IUserRoleRepository _userRoles;
        private readonly UserManager<IdentityUser> _identityUserManager;
        private readonly SignInManager<IdentityUser> _identitySignInManager;
        private readonly ITenantManager _tenantManager;
        private readonly INotificationRepository _notifications;
        private readonly IFolderRepository _folders;
        private readonly IFileRepository _files;
        private readonly IProfileRepository _profiles;
        private readonly ISettingRepository _settings;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;

        public UserManager(IUserRepository users, IRoleRepository roles, IUserRoleRepository userRoles, UserManager<IdentityUser> identityUserManager, SignInManager<IdentityUser> identitySignInManager, ITenantManager tenantManager, INotificationRepository notifications, IFolderRepository folders, IFileRepository files, IProfileRepository profiles, ISettingRepository settings, ISyncManager syncManager, ILogManager logger)
        {
            _users = users;
            _roles = roles;
            _userRoles = userRoles;
            _identityUserManager = identityUserManager;
            _identitySignInManager = identitySignInManager;
            _tenantManager = tenantManager;
            _notifications = notifications;
            _folders = folders;
            _files = files;
            _profiles = profiles;
            _settings = settings;
            _syncManager = syncManager;
            _logger = logger;
        }

        public User GetUser(int userid, int siteid)
        {
            User user = _users.GetUser(userid);
            if (user != null)
            {
                user.SiteId = siteid;
                user.Roles = GetUserRoles(user.UserId, user.SiteId);
            }
            return user;
        }

        public User GetUser(string username, int siteid)
        {
            return GetUser(username, "", siteid);
        }

        public User GetUser(string username, string email, int siteid)
        {
            User user = _users.GetUser(username, email);
            if (user != null)
            {
                user.SiteId = siteid;
                user.Roles = GetUserRoles(user.UserId, user.SiteId);
            }
            return user;
        }

        private string GetUserRoles(int userId, int siteId)
        {
            string roles = "";
            List<UserRole> userroles = _userRoles.GetUserRoles(userId, siteId).ToList();
            foreach (UserRole userrole in userroles)
            {
                roles += userrole.Role.Name + ";";
                if (userrole.Role.Name == RoleNames.Host && !userroles.Any(item => item.Role.Name == RoleNames.Admin))
                {
                    roles += RoleNames.Admin + ";";
                }
                if (userrole.Role.Name == RoleNames.Host && !userroles.Any(item => item.Role.Name == RoleNames.Registered))
                {
                    roles += RoleNames.Registered + ";";
                }
            }
            if (roles != "") roles = ";" + roles;
            return roles;
        }

        public async Task<User> AddUser(User user)
        {
            User User = null;
            var alias = _tenantManager.GetAlias();
            bool succeeded = false;
            string errors = "";

            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser == null)
            {
                if (string.IsNullOrEmpty(user.Password))
                {
                    // create random password ie. Jan-01-2023+12:00:00!
                    Random rnd = new Random();
                    user.Password = DateTime.UtcNow.ToString("MMM-dd-yyyy+HH:mm:ss", CultureInfo.InvariantCulture) + (char)rnd.Next(33, 47);
                }
                identityuser = new IdentityUser();
                identityuser.UserName = user.Username;
                identityuser.Email = user.Email;
                identityuser.EmailConfirmed = user.EmailConfirmed;
                var result = await _identityUserManager.CreateAsync(identityuser, user.Password);
                succeeded = result.Succeeded;
                if (!succeeded)
                {
                    errors = string.Join(", ", result.Errors.Select(e => e.Description));
                }
            }
            else
            {
                var result = await _identitySignInManager.CheckPasswordSignInAsync(identityuser, user.Password, false);
                succeeded = result.Succeeded;
                if (!succeeded)
                {
                    errors = "Password Not Valid For User";
                }
                user.EmailConfirmed = succeeded;
            }

            if (succeeded)
            {
                user.DisplayName = (user.DisplayName == null) ? user.Username : user.DisplayName;
                user.LastLoginOn = null;
                user.LastIPAddress = "";
                User = _users.AddUser(user);
                _syncManager.AddSyncEvent(alias.TenantId, EntityNames.User, User.UserId, SyncEventActions.Create);
            }
            else
            {
                _logger.Log(user.SiteId, LogLevel.Error, this, LogFunction.Create, "Unable To Add User {Username} - {Errors}", user.Username, errors);
            }

            if (User != null)
            {
                if (!user.EmailConfirmed)
                {
                    string token = await _identityUserManager.GenerateEmailConfirmationTokenAsync(identityuser);
                    string url = alias.Protocol + alias.Name + "/login?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token);
                    string body = "Dear " + user.DisplayName + ",\n\nIn Order To Complete The Registration Of Your User Account Please Click The Link Displayed Below:\n\n" + url + "\n\nThank You!";
                    var notification = new Notification(user.SiteId, User, "User Account Verification", body);
                    _notifications.AddNotification(notification);
                }
                else
                {
                    string url = alias.Protocol + alias.Name;
                    string body = "Dear " + user.DisplayName + ",\n\nA User Account Has Been Successfully Created For You With The Username " + user.Username + ". Please Visit " + url + " And Use The Login Option To Sign In. If You Do Not Know Your Password, Use The Forgot Password Option On The Login Page To Reset Your Account.\n\nThank You!";
                    var notification = new Notification(user.SiteId, User, "User Account Notification", body);
                    _notifications.AddNotification(notification);
                }

                User.Password = ""; // remove sensitive information
                _logger.Log(user.SiteId, LogLevel.Information, this, LogFunction.Create, "User Added {User}", User);
            }
            else
            {
                user.Password = ""; // remove sensitive information
                _logger.Log(user.SiteId, LogLevel.Error, this, LogFunction.Create, "Unable To Add User {User}", user);
            }

            return User;
        }

        public async Task<User> UpdateUser(User user)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null)
            {
                identityuser.Email = user.Email;
                var valid = true;
                if (user.Password != "")
                {
                    var validator = new PasswordValidator<IdentityUser>();
                    var result = await validator.ValidateAsync(_identityUserManager, null, user.Password);
                    valid = result.Succeeded;
                    if (valid)
                    {
                        identityuser.PasswordHash = _identityUserManager.PasswordHasher.HashPassword(identityuser, user.Password);
                    }
                }
                if (valid)
                {
                    await _identityUserManager.UpdateAsync(identityuser);

                    user = _users.UpdateUser(user);
                    _syncManager.AddSyncEvent(_tenantManager.GetAlias().TenantId, EntityNames.User, user.UserId, SyncEventActions.Update);
                    _syncManager.AddSyncEvent(_tenantManager.GetAlias().TenantId, EntityNames.User, user.UserId, SyncEventActions.Reload);
                    user.Password = ""; // remove sensitive information
                    _logger.Log(LogLevel.Information, this, LogFunction.Update, "User Updated {User}", user);
                }
                else
                {
                    _logger.Log(user.SiteId, LogLevel.Error, this, LogFunction.Update, "Unable To Update User {Username}. Password Does Not Meet Complexity Requirements.", user.Username);
                    user = null;
                }
            }

            return user;
        }

        public async Task DeleteUser(int userid, int siteid)
        {
            // remove user roles for site
            foreach (UserRole userrole in _userRoles.GetUserRoles(userid, siteid).ToList())
            {
                _userRoles.DeleteUserRole(userrole.UserRoleId);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "User Role Deleted {UserRole}", userrole);
            }

            // remove user folder for site
            var folder = _folders.GetFolder(siteid, $"Users/{userid}/");
            if (folder != null)
            {
                if (Directory.Exists(_folders.GetFolderPath(folder)))
                {
                    Directory.Delete(_folders.GetFolderPath(folder), true);
                }
                _folders.DeleteFolder(folder.FolderId);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "User Folder Deleted {Folder}", folder);
            }

            // delete user if they are not a member of any other sites
            if (!_userRoles.GetUserRoles(userid, -1).Any())
            {
                // get identity user
                var user = _users.GetUser(userid, false);
                IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
                if (identityuser != null)
                {
                    // delete identity user
                    var result = await _identityUserManager.DeleteAsync(identityuser);
                    if (result != null)
                    {
                        // delete user
                        _users.DeleteUser(userid);
                        _syncManager.AddSyncEvent(_tenantManager.GetAlias().TenantId, EntityNames.User, userid, SyncEventActions.Delete);
                        _syncManager.AddSyncEvent(_tenantManager.GetAlias().TenantId, EntityNames.User, userid, SyncEventActions.Reload);
                        _logger.Log(LogLevel.Information, this, LogFunction.Delete, "User Deleted {UserId}", userid, result.ToString());
                    }
                    else
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Delete, "Error Deleting User {UserId}", userid);
                    }
                }
            }
        }

        public async Task<User> LoginUser(User user, bool setCookie, bool isPersistent)
        {
            user.IsAuthenticated = false;

            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null)
            {
                var result = await _identitySignInManager.CheckPasswordSignInAsync(identityuser, user.Password, true);
                if (result.Succeeded)
                {
                    var LastIPAddress = user.LastIPAddress ?? "";

                    user = _users.GetUser(user.Username);
                    if (!user.IsDeleted)
                    {
                        if (user.TwoFactorRequired)
                        {
                            var token = await _identityUserManager.GenerateTwoFactorTokenAsync(identityuser, "Email");
                            user.TwoFactorCode = token;
                            user.TwoFactorExpiry = DateTime.UtcNow.AddMinutes(10);
                            _users.UpdateUser(user);

                            string body = "Dear " + user.DisplayName + ",\n\nYou requested a secure verification code to log in to your account. Please enter the secure verification code on the site:\n\n" + token +
                                "\n\nPlease note that the code is only valid for 10 minutes so if you are unable to take action within that time period, you should initiate a new login on the site." +
                                "\n\nThank You!";
                            var notification = new Notification(user.SiteId, user, "User Verification Code", body);
                            _notifications.AddNotification(notification);

                            _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Verification Notification Sent For {Username}", user.Username);
                            user.TwoFactorRequired = true;
                        }
                        else
                        {
                            user = _users.GetUser(identityuser.UserName);
                            if (user != null)
                            {
                                if (identityuser.EmailConfirmed)
                                {
                                    user.IsAuthenticated = true;
                                    user.LastLoginOn = DateTime.UtcNow;
                                    user.LastIPAddress = LastIPAddress;
                                    _users.UpdateUser(user);
                                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Login Successful {Username}", user.Username);

                                    if (setCookie)
                                    {
                                        await _identitySignInManager.SignInAsync(identityuser, isPersistent);
                                    }
                                }
                                else
                                {
                                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Not Verified {Username}", user.Username);
                                }
                            }
                        }
                    }
                    else
                    {
                        _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Login Failed - Account Deleted {Username}", user.Username);
                    }
                }
                else
                {
                    if (result.IsLockedOut)
                    {
                        var alias = _tenantManager.GetAlias();
                        user = _users.GetUser(user.Username);
                        string token = await _identityUserManager.GeneratePasswordResetTokenAsync(identityuser);
                        string url = alias.Protocol + alias.Name + "/reset?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token);
                        string body = "Dear " + user.DisplayName + ",\n\nYou attempted multiple times unsuccessfully to log in to your account and it is now locked out. Please wait a few minutes and then try again... or use the link below to reset your password:\n\n" + url +
                            "\n\nPlease note that the link is only valid for 24 hours so if you are unable to take action within that time period, you should initiate another password reset on the site." +
                            "\n\nThank You!";
                        var notification = new Notification(user.SiteId, user, "User Lockout", body);
                        _notifications.AddNotification(notification);
                        _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Lockout Notification Sent For {Username}", user.Username);
                    }
                    else
                    {
                        _logger.Log(LogLevel.Information, this, LogFunction.Security, "User Login Failed {Username}", user.Username);
                    }
                }
            }

            return user;
        }

        public async Task<User> VerifyEmail(User user, string token)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null && !string.IsNullOrEmpty(token))
            {
                var result = await _identityUserManager.ConfirmEmailAsync(identityuser, token);
                if (result.Succeeded)
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Email Verified For {Username}", user.Username);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Email Verification Failed For {Username} - Error {Error}", user.Username, string.Join(" ", result.Errors.ToList().Select(e => e.Description)));
                    user = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Email Verification Failed For {Username}And Token {Token}", user.Username, token);
                user = null;
            }
            return user;
        }
        public async Task ForgotPassword(User user)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null)
            {
                var alias = _tenantManager.GetAlias();
                user = _users.GetUser(user.Username);
                string token = await _identityUserManager.GeneratePasswordResetTokenAsync(identityuser);
                string url = alias.Protocol + alias.Name + "/reset?name=" + user.Username + "&token=" + WebUtility.UrlEncode(token);
                string body = "Dear " + user.DisplayName + ",\n\nYou recently requested to reset your password. Please use the link below to complete the process:\n\n" + url +
                    "\n\nPlease note that the link is only valid for 24 hours so if you are unable to take action within that time period, you should initiate another password reset on the site." +
                    "\n\nIf you did not request to reset your password you can safely ignore this message." +
                    "\n\nThank You!";

                var notification = new Notification(_tenantManager.GetAlias().SiteId, user, "User Password Reset", body);
                _notifications.AddNotification(notification);
                _logger.Log(LogLevel.Information, this, LogFunction.Security, "Password Reset Notification Sent For {Username}", user.Username);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Password Reset Notification Failed For {Username}", user.Username);
            }
        }

        public async Task<User> ResetPassword(User user, string token)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null && !string.IsNullOrEmpty(token))
            {
                var result = await _identityUserManager.ResetPasswordAsync(identityuser, token, user.Password);
                if (result.Succeeded)
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Password Reset For {Username}", user.Username);
                    user.Password = "";
                }
                else
                {
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "Password Reset Failed For {Username} - Error {Error}", user.Username, string.Join(" ", result.Errors.ToList().Select(e => e.Description)));
                    user = null;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Password Reset Failed For {Username} And Token {Token}", user.Username, token);
                user = null;
            }
            return user;
        }

        public User VerifyTwoFactor(User user, string token)
        {
            user = _users.GetUser(user.Username);
            if (user != null)
            {
                if (user.TwoFactorRequired && user.TwoFactorCode == token && DateTime.UtcNow < user.TwoFactorExpiry)
                {
                    user.IsAuthenticated = true;
                }
            }
            return user;
        }

        public async Task<User> LinkExternalAccount(User user, string token, string type, string key, string name)
        {
            IdentityUser identityuser = await _identityUserManager.FindByNameAsync(user.Username);
            if (identityuser != null && !string.IsNullOrEmpty(token))
            {
                var result = await _identityUserManager.ConfirmEmailAsync(identityuser, token);
                if (result.Succeeded)
                {
                    // make LoginProvider multi-tenant aware
                    type += ":" + user.SiteId.ToString();
                    await _identityUserManager.AddLoginAsync(identityuser, new UserLoginInfo(type, key, name));
                    _logger.Log(LogLevel.Information, this, LogFunction.Security, "External Login Linkage Successful For {Username} And Provider {Provider}", user.Username, type);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "External Login Linkage Failed For {Username} - Error {Error}", user.Username, string.Join(" ", result.Errors.ToList().Select(e => e.Description)));
                    user = null;
                }
            }
            return user;
        }

        public async Task<bool> ValidatePassword(string password)
        {
            var validator = new PasswordValidator<IdentityUser>();
            var result = await validator.ValidateAsync(_identityUserManager, null, password);
            return result.Succeeded;
        }

        public async Task<bool> ImportUsers(int siteId, int fileId)
        {
            var success = true;
            int users = 0;

            var file = _files.GetFile(fileId);
            if (file != null)
            {
                var path = _files.GetFilePath(file);
                if (System.IO.File.Exists(path))
                {
                    var roles = _roles.GetRoles(siteId).ToList();
                    var profiles = _profiles.GetProfiles(siteId).ToList();

                    try
                    {
                        string row;
                        using (var reader = new StreamReader(path))
                        {
                            // get header row
                            row = reader.ReadLine();
                            var header = row.Replace("\"", "").Split(',');

                            row = reader.ReadLine();
                            while (row != null)
                            {
                                var values = row.Replace("\"", "").Split(',');

                                if (values.Length > 3)
                                {
                                    // user
                                    var user = _users.GetUser(values[1], values[0]);
                                    if (user == null)
                                    {
                                        user = new User();
                                        user.SiteId = siteId;
                                        user.Email = values[0];
                                        user.Username = (!string.IsNullOrEmpty(values[1])) ? values[1] : user.Email;
                                        user.DisplayName = (!string.IsNullOrEmpty(values[2])) ? values[2] : user.Username;
                                        user = await AddUser(user);
                                        if (user == null)
                                        {
                                            _logger.Log(LogLevel.Error, this, LogFunction.Create, "Error Creating User {Email}", values[0]);
                                            success = false;
                                        }
                                    }

                                    if (user != null && !string.IsNullOrEmpty(values[3]))
                                    {
                                        // roles (comma delimited)
                                        foreach (var rolename in values[3].Split(','))
                                        {
                                            var role = roles.FirstOrDefault(item => item.Name == rolename);
                                            if (role == null)
                                            {
                                                role = new Role();
                                                role.SiteId = siteId;
                                                role.Name = rolename;
                                                role.Description = rolename;
                                                role = _roles.AddRole(role);
                                                roles.Add(role);
                                            }
                                            if (role != null)
                                            {
                                                var userrole = _userRoles.GetUserRole(user.UserId, role.RoleId, false);
                                                if (userrole == null)
                                                {
                                                    userrole = new UserRole();
                                                    userrole.UserId = user.UserId;
                                                    userrole.RoleId = role.RoleId;
                                                    _userRoles.AddUserRole(userrole);
                                                }
                                            }
                                        }
                                    }

                                    if (user != null && values.Length > 4)
                                    {
                                        var settings = _settings.GetSettings(EntityNames.User, user.UserId);
                                        for (int index = 4; index < values.Length - 1; index++)
                                        {
                                            if (header.Length > index && !string.IsNullOrEmpty(values[index]))
                                            {
                                                var profile = profiles.FirstOrDefault(item => item.Name == header[index]);
                                                if (profile != null)
                                                {
                                                    var setting = settings.FirstOrDefault(item => item.SettingName == profile.Name);
                                                    if (setting == null)
                                                    {
                                                        setting = new Setting();
                                                        setting.EntityName = EntityNames.User;
                                                        setting.EntityId = user.UserId;
                                                        setting.SettingName = profile.Name;
                                                        setting.SettingValue = values[index];
                                                        _settings.AddSetting(setting);
                                                    }
                                                    else
                                                    {
                                                        if (setting.SettingValue != values[index])
                                                        {
                                                            setting.SettingValue = values[index];
                                                            _settings.UpdateSetting(setting);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    users++;
                                }

                                row = reader.ReadLine();
                            }
                        }

                        _logger.Log(LogLevel.Information, this, LogFunction.Create, "{Users} Users Imported", users);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(LogLevel.Error, this, LogFunction.Create, ex, "Error Importing User Import File {SiteId} {FileId}", siteId, fileId);
                        success = false;
                    }
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Create,"User Import File Does Not Exist {Path}", path);
                    success = false;
                }
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Create, "User Import File Does Not Exist {SiteId} {FileId}", siteId, fileId);
                success = false;
            }

            return success;
        }
    }
}
