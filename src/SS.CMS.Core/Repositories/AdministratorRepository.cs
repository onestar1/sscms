﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using SqlKata;
using SS.CMS.Abstractions;
using SS.CMS.Abstractions.Enums;
using SS.CMS.Abstractions.Models;
using SS.CMS.Abstractions.Repositories;
using SS.CMS.Abstractions.Services;
using SS.CMS.Core.Cache;
using SS.CMS.Core.Security;
using SS.CMS.Core.Services;
using SS.CMS.Data;
using SS.CMS.Utils;
using SS.CMS.Utils.Enumerations;

namespace SS.CMS.Core.Repositories
{
    public partial class AdministratorRepository : IAdministratorRepository
    {
        private static readonly string CacheKey = StringUtils.GetCacheKey(nameof(AccessTokenRepository));
        private readonly Repository<AdministratorInfo> _repository;
        private readonly ISettingsManager _settingsManager;
        private readonly ICacheManager _cacheManager;
        private readonly IAdministratorsInRolesRepository _administratorsInRolesRepository;
        private readonly IPermissionsInRolesRepository _permissionsInRolesRepository;
        private readonly ISitePermissionsRepository _sitePermissionsRepository;

        public AdministratorRepository(ISettingsManager settingsManager, ICacheManager cacheManager, IAdministratorsInRolesRepository administratorsInRolesRepository, IPermissionsInRolesRepository permissionsInRolesRepository, ISitePermissionsRepository sitePermissionsRepository)
        {
            _repository = new Repository<AdministratorInfo>(new Db(settingsManager.DatabaseType, settingsManager.DatabaseConnectionString));
            _settingsManager = settingsManager;
            _cacheManager = cacheManager;
            _administratorsInRolesRepository = administratorsInRolesRepository;
            _permissionsInRolesRepository = permissionsInRolesRepository;
            _sitePermissionsRepository = sitePermissionsRepository;
        }

        public IDb Db => _repository.Db;

        public string TableName => _repository.TableName;
        public List<TableColumn> TableColumns => _repository.TableColumns;

        private static class Attr
        {
            public const string Id = nameof(AdministratorInfo.Id);
            public const string UserName = nameof(AdministratorInfo.UserName);
            public const string DepartmentId = nameof(AdministratorInfo.DepartmentId);
            public const string AreaId = nameof(AdministratorInfo.AreaId);
            public const string Mobile = nameof(AdministratorInfo.Mobile);
            public const string Email = nameof(AdministratorInfo.Email);
            public const string Password = nameof(AdministratorInfo.Password);
            public const string PasswordFormat = nameof(AdministratorInfo.PasswordFormat);
            public const string PasswordSalt = nameof(AdministratorInfo.PasswordSalt);
            public const string SiteId = nameof(AdministratorInfo.SiteId);
            public const string SiteIdCollection = nameof(AdministratorInfo.SiteIdCollection);
            public const string IsLockedOut = "IsLockedOut";
        }

        public IEnumerable<AdministratorInfo> GetAll(Query query)
        {
            return _repository.GetAll(query);
        }

        public int GetCount(Query query)
        {
            return _repository.Count(query);
        }

        public int GetCount()
        {
            return _repository.Count();
        }

        public int Insert(AdministratorInfo adminInfo, out string errorMessage)
        {
            if (!InsertValidate(adminInfo.UserName, adminInfo.Password, adminInfo.Email, adminInfo.Mobile, out errorMessage)) return 0;

            try
            {
                adminInfo.CreationDate = DateTime.Now;
                adminInfo.PasswordFormat = PasswordFormat.Encrypted.Value;
                adminInfo.Password = EncodePassword(adminInfo.Password, PasswordFormat.Parse(adminInfo.PasswordFormat), out var passwordSalt);
                adminInfo.PasswordSalt = passwordSalt;

                var identity = _repository.Insert(adminInfo);

                if (identity <= 0) return 0;

                _administratorsInRolesRepository.AddUserToRole(adminInfo.UserName, EPredefinedRoleUtils.GetValue(EPredefinedRole.Administrator));

                return identity;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return 0;
            }
        }

        public bool Update(AdministratorInfo administratorInfo, out string errorMessage)
        {
            var adminInfo = GetAdminInfoByUserId(administratorInfo.Id);

            administratorInfo.Password = adminInfo.Password;
            administratorInfo.PasswordFormat = adminInfo.PasswordFormat;
            administratorInfo.PasswordSalt = adminInfo.PasswordSalt;

            if (!UpdateValidate(administratorInfo, adminInfo.UserName, adminInfo.Email, adminInfo.Mobile, out errorMessage)) return false;

            var updated = _repository.Update(administratorInfo);

            if (updated)
            {
                UpdateCache(administratorInfo);
            }

            return updated;
        }

        public bool UpdateLastActivityDateAndCountOfFailedLogin(AdministratorInfo adminInfo)
        {
            if (adminInfo == null) return false;

            adminInfo.LastActivityDate = DateTime.Now;
            adminInfo.CountOfFailedLogin += 1;

            var updated = Update(adminInfo, out _);
            if (updated)
            {
                UpdateCache(adminInfo);
            }
            return updated;
        }

        public void UpdateLastActivityDateAndCountOfLogin(AdministratorInfo adminInfo)
        {
            if (adminInfo == null) return;

            adminInfo.LastActivityDate = DateTime.Now;
            adminInfo.CountOfLogin += 1;
            adminInfo.CountOfFailedLogin = 0;

            var updated = Update(adminInfo, out _);
            if (updated)
            {
                UpdateCache(adminInfo);
            }
        }

        public void UpdateSiteIdCollection(AdministratorInfo adminInfo, string siteIdCollection)
        {
            if (adminInfo == null) return;

            adminInfo.SiteIdCollection = siteIdCollection;

            _repository.Update(Q
                    .Set(Attr.SiteIdCollection, siteIdCollection)
                    .Where(Attr.Id, adminInfo.Id)
                );

            UpdateCache(adminInfo);
        }

        public List<int> UpdateSiteId(AdministratorInfo adminInfo, int siteId)
        {
            if (adminInfo == null) return null;

            var siteIdListLatestAccessed = TranslateUtils.StringCollectionToIntList(adminInfo.SiteIdCollection);
            if (adminInfo.SiteId != siteId || siteIdListLatestAccessed.FirstOrDefault() != siteId)
            {
                siteIdListLatestAccessed.Remove(siteId);
                siteIdListLatestAccessed.Insert(0, siteId);

                adminInfo.SiteIdCollection = TranslateUtils.ObjectCollectionToString(siteIdListLatestAccessed);
                adminInfo.SiteId = siteId;

                _repository.Update(Q
                    .Set(Attr.SiteId, siteId)
                    .Set(Attr.SiteIdCollection, TranslateUtils.ObjectCollectionToString(siteIdListLatestAccessed))
                    .Where(Attr.Id, adminInfo.Id)
                );

                RemoveCache(adminInfo);
            }

            return siteIdListLatestAccessed;
        }

        private void ChangePassword(AdministratorInfo adminInfo, PasswordFormat passwordFormat, string passwordSalt, string password)
        {
            adminInfo.Password = password;
            adminInfo.PasswordFormat = passwordFormat.Value;
            adminInfo.PasswordSalt = passwordSalt;

            _repository.Update(adminInfo, Attr.Password, Attr.PasswordFormat, Attr.PasswordSalt);

            RemoveCache(adminInfo);
        }

        public bool Delete(AdministratorInfo adminInfo)
        {
            if (adminInfo == null) return false;

            var deleted = _repository.Delete(adminInfo.Id);

            if (deleted)
            {
                RemoveCache(adminInfo);

                _administratorsInRolesRepository.RemoveUser(adminInfo.UserName);
            }

            return deleted;
        }

        public void Lock(List<int> userIdList)
        {
            _repository.Update(Q
                .Set(Attr.IsLockedOut, true.ToString())
                .WhereIn(Attr.Id, userIdList)
            );

            ClearCache();
        }

        public void UnLock(List<int> userIdList)
        {
            _repository.Update(Q
                .Set(Attr.IsLockedOut, false.ToString())
                .WhereIn(Attr.Id, userIdList)
            );

            ClearCache();
        }

        private AdministratorInfo GetByAccount(string account)
        {
            var administratorInfo = GetByUserName(account);
            if (administratorInfo != null) return administratorInfo;
            if (StringUtils.IsMobile(account)) return GetByMobile(account);
            if (StringUtils.IsEmail(account)) return GetByEmail(account);

            return null;
        }

        public AdministratorInfo GetByUserId(int userId)
        {
            return _repository.Get(userId);
        }

        public AdministratorInfo GetByUserName(string userName)
        {
            return _repository.Get(Q.Where(Attr.UserName, userName));
        }

        public AdministratorInfo GetByMobile(string mobile)
        {
            return _repository.Get(Q.Where(Attr.Mobile, mobile));
        }

        public AdministratorInfo GetByEmail(string email)
        {
            return _repository.Get(Q.Where(Attr.Email, email));
        }

        public bool IsUserNameExists(string adminName)
        {
            return _repository.Exists(Q.Where(Attr.UserName, adminName));
        }

        public bool IsEmailExists(string email)
        {
            if (string.IsNullOrEmpty(email)) return false;

            var exists = IsUserNameExists(email);
            if (exists) return true;

            return _repository.Exists(Q
                .Where(Attr.Email, email));
        }

        public bool IsMobileExists(string mobile)
        {
            if (string.IsNullOrEmpty(mobile)) return false;

            var exists = IsUserNameExists(mobile);
            if (exists) return true;

            return _repository.Exists(Q
                .Where(Attr.Mobile, mobile));
        }

        public int GetCountByAreaId(int areaId)
        {
            return _repository.Count(Q
                .Where(Attr.AreaId, areaId));
        }

        public int GetCountByDepartmentId(int departmentId)
        {
            return _repository.Count(Q
                .Where(Attr.DepartmentId, departmentId));
        }

        /// <summary>
        /// 获取管理员用户名列表。
        /// </summary>
        /// <returns>
        /// 管理员用户名列表。
        /// </returns>
        public async Task<IEnumerable<string>> GetUserNameListAsync()
        {
            return await _repository.GetAllAsync<string>(Q.Select(Attr.UserName));
        }

        public IEnumerable<string> GetUserNameList(int departmentId)
        {
            return _repository.GetAll<string>(Q
                .Select(Attr.UserName)
                .Where(Attr.DepartmentId, departmentId));
        }

        private bool UpdateValidate(AdministratorInfo adminInfoToUpdate, string userName, string email, string mobile, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (adminInfoToUpdate.UserName != null && adminInfoToUpdate.UserName != userName)
            {
                if (string.IsNullOrEmpty(adminInfoToUpdate.UserName))
                {
                    errorMessage = "用户名不能为空";
                    return false;
                }
                if (adminInfoToUpdate.UserName.Length < _settingsManager.ConfigInfo.AdminUserNameMinLength)
                {
                    errorMessage = $"用户名长度必须大于等于{_settingsManager.ConfigInfo.AdminUserNameMinLength}";
                    return false;
                }
                if (IsUserNameExists(adminInfoToUpdate.UserName))
                {
                    errorMessage = "用户名已存在，请更换用户名";
                    return false;
                }
            }

            if (adminInfoToUpdate.Mobile != null && adminInfoToUpdate.Mobile != mobile)
            {
                if (!string.IsNullOrEmpty(adminInfoToUpdate.Mobile) && IsMobileExists(adminInfoToUpdate.Mobile))
                {
                    errorMessage = "手机号码已被注册，请更换手机号码";
                    return false;
                }
            }

            if (adminInfoToUpdate.Email != null && adminInfoToUpdate.Email != email)
            {
                if (!string.IsNullOrEmpty(adminInfoToUpdate.Email) && IsEmailExists(adminInfoToUpdate.Email))
                {
                    errorMessage = "电子邮件地址已被注册，请更换邮箱";
                    return false;
                }
            }

            return true;
        }

        private bool InsertValidate(string userName, string password, string email, string mobile, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (string.IsNullOrEmpty(userName))
            {
                errorMessage = "用户名不能为空";
                return false;
            }
            if (userName.Length < _settingsManager.ConfigInfo.AdminUserNameMinLength)
            {
                errorMessage = $"用户名长度必须大于等于{_settingsManager.ConfigInfo.AdminUserNameMinLength}";
                return false;
            }
            if (IsUserNameExists(userName))
            {
                errorMessage = "用户名已存在，请更换用户名";
                return false;
            }

            if (string.IsNullOrEmpty(password))
            {
                errorMessage = "密码不能为空";
                return false;
            }
            if (password.Length < _settingsManager.ConfigInfo.AdminPasswordMinLength)
            {
                errorMessage = $"密码长度必须大于等于{_settingsManager.ConfigInfo.AdminPasswordMinLength}";
                return false;
            }
            if (
                !EUserPasswordRestrictionUtils.IsValid(password,
                    _settingsManager.ConfigInfo.AdminPasswordRestriction))
            {
                errorMessage =
                    $"密码不符合规则，请包含{EUserPasswordRestrictionUtils.GetText(EUserPasswordRestrictionUtils.GetEnumType(_settingsManager.ConfigInfo.AdminPasswordRestriction))}";
                return false;
            }

            if (!string.IsNullOrEmpty(mobile) && IsMobileExists(mobile))
            {
                errorMessage = "手机号码已被注册，请更换手机号码";
                return false;
            }
            if (!string.IsNullOrEmpty(email) && IsEmailExists(email))
            {
                errorMessage = "电子邮件地址已被注册，请更换邮箱";
                return false;
            }

            return true;
        }

        public bool ChangePassword(AdministratorInfo adminInfo, string password, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrEmpty(password))
            {
                errorMessage = "密码不能为空";
                return false;
            }
            if (password.Length < _settingsManager.ConfigInfo.AdminPasswordMinLength)
            {
                errorMessage = $"密码长度必须大于等于{_settingsManager.ConfigInfo.AdminPasswordMinLength}";
                return false;
            }
            if (
                !EUserPasswordRestrictionUtils.IsValid(password, _settingsManager.ConfigInfo.AdminPasswordRestriction))
            {
                errorMessage =
                    $"密码不符合规则，请包含{EUserPasswordRestrictionUtils.GetText(EUserPasswordRestrictionUtils.GetEnumType(_settingsManager.ConfigInfo.AdminPasswordRestriction))}";
                return false;
            }

            password = EncodePassword(password, PasswordFormat.Encrypted, out var passwordSalt);
            ChangePassword(adminInfo, PasswordFormat.Encrypted, passwordSalt, password);
            return true;
        }

        public bool Validate(string account, string password, bool isPasswordMd5, out string userName, out string errorMessage)
        {
            userName = string.Empty;
            errorMessage = string.Empty;

            if (string.IsNullOrEmpty(account))
            {
                errorMessage = "账号不能为空";
                return false;
            }
            if (string.IsNullOrEmpty(password))
            {
                errorMessage = "密码不能为空";
                return false;
            }

            var adminInfo = GetByAccount(account);
            if (string.IsNullOrEmpty(adminInfo?.UserName))
            {
                errorMessage = "帐号或密码错误";
                return false;
            }

            userName = adminInfo.UserName;

            if (adminInfo.Locked)
            {
                errorMessage = "此账号被锁定，无法登录";
                return false;
            }

            if (_settingsManager.ConfigInfo.IsAdminLockLogin)
            {
                if (adminInfo.CountOfFailedLogin > 0 &&
                    adminInfo.CountOfFailedLogin >= _settingsManager.ConfigInfo.AdminLockLoginCount)
                {
                    var lockType = EUserLockTypeUtils.GetEnumType(_settingsManager.ConfigInfo.AdminLockLoginType);
                    if (lockType == EUserLockType.Forever)
                    {
                        errorMessage = "此账号错误登录次数过多，已被永久锁定";
                        return false;
                    }
                    if (lockType == EUserLockType.Hours)
                    {
                        if (adminInfo.LastActivityDate.HasValue)
                        {
                            var ts = new TimeSpan(DateTime.Now.Ticks - adminInfo.LastActivityDate.Value.Ticks);
                            var hours = Convert.ToInt32(_settingsManager.ConfigInfo.AdminLockLoginHours - ts.TotalHours);
                            if (hours > 0)
                            {
                                errorMessage =
                                    $"此账号错误登录次数过多，已被锁定，请等待{hours}小时后重试";
                                return false;
                            }
                        }
                    }
                }
            }

            if (CheckPassword(password, isPasswordMd5, adminInfo.Password, PasswordFormat.Parse(adminInfo.PasswordFormat), adminInfo.PasswordSalt))
                return true;

            errorMessage = "账号或密码错误";
            return false;
        }

        public bool CheckPassword(string password, bool isPasswordMd5, string dbPassword, PasswordFormat passwordFormat, string passwordSalt)
        {
            var decodePassword = DecodePassword(dbPassword, passwordFormat, passwordSalt);
            if (isPasswordMd5)
            {
                return password == AuthUtils.Md5ByString(decodePassword);
            }
            return password == decodePassword;
        }

        private static string EncodePassword(string password, PasswordFormat passwordFormat, out string passwordSalt)
        {
            var retVal = string.Empty;
            passwordSalt = string.Empty;

            if (passwordFormat == PasswordFormat.Clear)
            {
                retVal = password;
            }
            else if (passwordFormat == PasswordFormat.Hashed)
            {
                passwordSalt = GenerateSalt();

                var src = Encoding.Unicode.GetBytes(password);
                var buffer2 = Convert.FromBase64String(passwordSalt);
                var dst = new byte[buffer2.Length + src.Length];
                Buffer.BlockCopy(buffer2, 0, dst, 0, buffer2.Length);
                Buffer.BlockCopy(src, 0, dst, buffer2.Length, src.Length);
                var algorithm = HashAlgorithm.Create("SHA1");
                if (algorithm == null) return retVal;
                var inArray = algorithm.ComputeHash(dst);

                retVal = Convert.ToBase64String(inArray);
            }
            else if (passwordFormat == PasswordFormat.Encrypted)
            {
                passwordSalt = GenerateSalt();

                var encrypt = new DesEncryptor
                {
                    InputString = password,
                    EncryptKey = passwordSalt
                };
                encrypt.DesEncrypt();

                retVal = encrypt.OutString;
            }
            return retVal;
        }

        private static string GenerateSalt()
        {
            var data = new byte[0x10];
            new RNGCryptoServiceProvider().GetBytes(data);
            return Convert.ToBase64String(data);
        }

        private static string DecodePassword(string password, PasswordFormat passwordFormat, string passwordSalt)
        {
            var retVal = string.Empty;
            if (passwordFormat == PasswordFormat.Clear)
            {
                retVal = password;
            }
            else if (passwordFormat == PasswordFormat.Hashed)
            {
                throw new Exception("can not decode hashed password");
            }
            else if (passwordFormat == PasswordFormat.Encrypted)
            {
                var encrypt = new DesEncryptor
                {
                    InputString = password,
                    DecryptKey = passwordSalt
                };
                encrypt.DesDecrypt();

                retVal = encrypt.OutString;
            }
            return retVal;
        }
    }
}