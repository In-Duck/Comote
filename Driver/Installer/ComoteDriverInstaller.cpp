#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif

#include <Windows.h>
#include <bcrypt.h>
#include <cfgmgr32.h>
#include <devguid.h>
#include <devpkey.h>
#include <newdev.h>
#include <setupapi.h>
#include <shlobj.h>
#include <softpub.h>
#include <wincrypt.h>
#include <wintrust.h>
#include <mssip.h>
#include <mscat.h>
#include <sddl.h>
#include <Aclapi.h>
#include <winternl.h>

#include "ComoteReleaseManifestPin.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cwctype>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <map>
#include <optional>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

#pragma comment(lib, "Advapi32.lib")
#pragma comment(lib, "Bcrypt.lib")
#pragma comment(lib, "Cfgmgr32.lib")
#pragma comment(lib, "Crypt32.lib")
#pragma comment(lib, "Newdev.lib")
#pragma comment(lib, "Ole32.lib")
#pragma comment(lib, "Setupapi.lib")
#pragma comment(lib, "Shell32.lib")
#pragma comment(lib, "Wintrust.lib")
#pragma comment(lib, "Uuid.lib")

namespace fs = std::filesystem;

namespace
{
constexpr wchar_t kHardwareId[] =
    L"ROOT\\COMOTEVIRTUALHID_PHASE2";
constexpr wchar_t kRootInstanceId[] =
    L"ROOT\\COMOTEVIRTUALHID_PHASE2\\COMOTE_PHASE2";
constexpr wchar_t kServiceName[] = L"ComoteVirtualHidPhase2";
constexpr wchar_t kProvider[] = L"Comote";
constexpr wchar_t kInfName[] = L"ComoteVirtualHidPhase2.inf";
constexpr wchar_t kCatName[] = L"ComoteVirtualHidPhase2.cat";
constexpr wchar_t kSysName[] = L"ComoteVirtualHidPhase2.sys";
constexpr wchar_t kMutexName[] =
    L"Global\\ComotePhase2DriverInstaller-v1";
constexpr char kManifestHeader[] =
    "COMOTE-PHASE2-PACKAGE-MANIFEST-V1";
constexpr char kStateHeader[] =
    "COMOTE-PHASE2-INSTALLER-STATE-V2";

#ifndef COMOTE_INSTALLER_VM_TEST
#define COMOTE_INSTALLER_VM_TEST 0
#endif

const GUID kControlInterfaceGuid = {
    0xba2bc8d8,
    0x8d1b,
    0x48e4,
    {0x8e, 0xa7, 0x79, 0x13, 0x9b, 0x13, 0x07, 0xa8}};

enum class ExitCode : int
{
    Success = 0,
    Usage = 2,
    NotElevated = 3,
    UnsupportedPlatform = 4,
    InvalidManifest = 10,
    PackageHashMismatch = 11,
    SignatureInvalid = 12,
    InfIdentityInvalid = 13,
    NotInstalled = 20,
    AlreadyInstalled = 21,
    Conflict = 22,
    RecoveryRequired = 23,
    DeviceCreateFailed = 30,
    DriverInstallFailed = 31,
    VerificationFailed = 32,
    DeviceRemoveFailed = 33,
    PackageRemoveFailed = 34,
    ServiceCleanupFailed = 35,
    RebootRequired = 36,
    IoFailure = 40,
    InternalError = 50
};

class InstallerError final : public std::runtime_error
{
public:
    InstallerError(const ExitCode code, const std::string& message)
        : std::runtime_error(message), code_(code)
    {
    }

    [[nodiscard]] ExitCode code() const noexcept
    {
        return code_;
    }

private:
    ExitCode code_;
};

template <typename T, typename Deleter>
class UniqueResource final
{
public:
    UniqueResource() noexcept = default;
    explicit UniqueResource(T value) noexcept : value_(value)
    {
    }
    ~UniqueResource()
    {
        reset();
    }
    UniqueResource(const UniqueResource&) = delete;
    UniqueResource& operator=(const UniqueResource&) = delete;
    UniqueResource(UniqueResource&& other) noexcept : value_(other.release())
    {
    }
    UniqueResource& operator=(UniqueResource&& other) noexcept
    {
        if (this != &other)
        {
            reset();
            value_ = other.release();
        }
        return *this;
    }
    [[nodiscard]] T get() const noexcept
    {
        return value_;
    }
    [[nodiscard]] explicit operator bool() const noexcept
    {
        return value_ != T{};
    }
    [[nodiscard]] T release() noexcept
    {
        const T value = value_;
        value_ = T{};
        return value;
    }
    void reset(T value = T{}) noexcept
    {
        if (value_ != T{})
        {
            Deleter{}(value_);
        }
        value_ = value;
    }

private:
    T value_{};
};

struct HandleDeleter
{
    void operator()(const HANDLE handle) const noexcept
    {
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE)
        {
            (void)CloseHandle(handle);
        }
    }
};

struct ModuleDeleter
{
    void operator()(const HMODULE module) const noexcept
    {
        if (module != nullptr)
        {
            (void)FreeLibrary(module);
        }
    }
};

struct LocalMemoryDeleter
{
    void operator()(const HLOCAL memory) const noexcept
    {
        if (memory != nullptr)
        {
            (void)LocalFree(memory);
        }
    }
};

struct FindHandleDeleter
{
    void operator()(const HANDLE handle) const noexcept
    {
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE)
        {
            (void)FindClose(handle);
        }
    }
};

struct MutexHandleDeleter
{
    void operator()(const HANDLE handle) const noexcept
    {
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE)
        {
            (void)ReleaseMutex(handle);
            (void)CloseHandle(handle);
        }
    }
};

struct DeviceInfoSetDeleter
{
    void operator()(const HDEVINFO set) const noexcept
    {
        if (set != nullptr && set != INVALID_HANDLE_VALUE)
        {
            (void)SetupDiDestroyDeviceInfoList(set);
        }
    }
};

struct ServiceHandleDeleter
{
    void operator()(const SC_HANDLE handle) const noexcept
    {
        if (handle != nullptr)
        {
            (void)CloseServiceHandle(handle);
        }
    }
};

struct InfHandleDeleter
{
    void operator()(const HINF handle) const noexcept
    {
        if (handle != INVALID_HANDLE_VALUE)
        {
            SetupCloseInfFile(handle);
        }
    }
};

struct BcryptAlgorithmDeleter
{
    void operator()(const BCRYPT_ALG_HANDLE handle) const noexcept
    {
        if (handle != nullptr)
        {
            (void)BCryptCloseAlgorithmProvider(handle, 0);
        }
    }
};

struct BcryptHashDeleter
{
    void operator()(const BCRYPT_HASH_HANDLE handle) const noexcept
    {
        if (handle != nullptr)
        {
            (void)BCryptDestroyHash(handle);
        }
    }
};

struct CatalogAdminDeleter
{
    void operator()(const HCATADMIN handle) const noexcept
    {
        if (handle != nullptr)
        {
            (void)CryptCATAdminReleaseContext(handle, 0);
        }
    }
};

struct CatalogHandleDeleter
{
    void operator()(const HANDLE handle) const noexcept
    {
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE)
        {
            (void)CryptCATClose(handle);
        }
    }
};

using UniqueHandle = UniqueResource<HANDLE, HandleDeleter>;
using UniqueLocalMemory = UniqueResource<HLOCAL, LocalMemoryDeleter>;
using UniqueModule = UniqueResource<HMODULE, ModuleDeleter>;
using UniqueFindHandle = UniqueResource<HANDLE, FindHandleDeleter>;
using UniqueMutexHandle = UniqueResource<HANDLE, MutexHandleDeleter>;
using UniqueDeviceInfoSet =
    UniqueResource<HDEVINFO, DeviceInfoSetDeleter>;
using UniqueServiceHandle =
    UniqueResource<SC_HANDLE, ServiceHandleDeleter>;
using UniqueInfHandle = UniqueResource<HINF, InfHandleDeleter>;
using UniqueBcryptAlgorithm =
    UniqueResource<BCRYPT_ALG_HANDLE, BcryptAlgorithmDeleter>;
using UniqueBcryptHash =
    UniqueResource<BCRYPT_HASH_HANDLE, BcryptHashDeleter>;
using UniqueCatalogAdmin =
    UniqueResource<HCATADMIN, CatalogAdminDeleter>;
using UniqueCatalogHandle =
    UniqueResource<HANDLE, CatalogHandleDeleter>;

struct Manifest
{
    std::wstring hardwareId;
    std::wstring rootInstanceId;
    std::wstring serviceName;
    std::wstring provider;
    std::string infSha256;
    std::string catSha256;
    std::string sysSha256;
    std::string manifestSha256;
    std::uint64_t infSize{};
    std::uint64_t catSize{};
    std::uint64_t sysSize{};
};

struct InstallerState
{
    std::string status;
    std::string operation;
    std::string manifestSha256;
    std::wstring hardwareId;
    std::wstring rootInstanceId;
    std::wstring serviceName;
    std::wstring publishedInf;
    std::wstring stagePath;
    std::string infSha256;
    std::string catSha256;
    std::string sysSha256;
    std::uint32_t bootId{};
    bool needsReboot{};
};

struct PackagePaths
{
    fs::path directory;
    fs::path inf;
    fs::path cat;
    fs::path sys;
};

struct DeviceRecord
{
    std::wstring instanceId;
    std::vector<std::wstring> hardwareIds;
    std::wstring serviceName;
    std::wstring publishedInf;
    std::wstring enumeratorName;
    GUID classGuid{};
    DEVINST devInst{};
    DWORD status{};
    ULONG problem{};
};

struct ServiceRecord
{
    bool exists{};
    DWORD state{};
    DWORD serviceType{};
    DWORD startType{};
    std::wstring binaryPath;
};

struct Inventory
{
    std::vector<DeviceRecord> roots;
    std::vector<fs::path> candidatePublishedInfs;
    std::optional<fs::path> matchingPublishedInf;
    ServiceRecord service;
    std::vector<std::wstring> interfaceInstances;
};

[[nodiscard]] std::string NarrowAscii(const std::wstring& value)
{
    std::string result;
    result.reserve(value.size());
    for (const wchar_t ch : value)
    {
        if (ch > 0x7f)
        {
            throw InstallerError(
                ExitCode::InvalidManifest,
                "Manifest and state values must be ASCII.");
        }
        result.push_back(static_cast<char>(ch));
    }
    return result;
}

[[nodiscard]] std::wstring WidenAscii(const std::string& value)
{
    std::wstring result;
    result.reserve(value.size());
    for (const unsigned char ch : value)
    {
        if (ch > 0x7f)
        {
            throw InstallerError(
                ExitCode::InvalidManifest,
                "Manifest and state values must be ASCII.");
        }
        result.push_back(static_cast<wchar_t>(ch));
    }
    return result;
}

[[nodiscard]] bool EqualsInsensitive(
    const std::wstring& left,
    const std::wstring& right)
{
    return _wcsicmp(left.c_str(), right.c_str()) == 0;
}

[[nodiscard]] bool StartsWithInsensitive(
    const std::wstring& value,
    const std::wstring& prefix)
{
    return value.size() >= prefix.size() &&
        _wcsnicmp(value.c_str(), prefix.c_str(), prefix.size()) == 0;
}

[[nodiscard]] bool EndsWithInsensitive(
    const std::wstring& value,
    const std::wstring& suffix)
{
    return value.size() >= suffix.size() &&
        _wcsicmp(
            value.c_str() + value.size() - suffix.size(),
            suffix.c_str()) == 0;
}

[[nodiscard]] std::string Hex(
    const std::vector<unsigned char>& bytes)
{
    std::ostringstream stream;
    stream << std::uppercase << std::hex << std::setfill('0');
    for (const unsigned char byte : bytes)
    {
        stream << std::setw(2) << static_cast<unsigned int>(byte);
    }
    return stream.str();
}

[[nodiscard]] bool IsSha256(const std::string& value)
{
    return value.size() == 64 &&
        std::all_of(
            value.begin(),
            value.end(),
            [](const unsigned char ch) {
                const int upper = std::toupper(ch);
                return std::isdigit(ch) != 0 ||
                    (upper >= 'A' && upper <= 'F');
            });
}

[[nodiscard]] std::uint64_t ParseDecimalSize(const std::string& value)
{
    if (value.empty() || value.size() > 20U ||
        !std::all_of(
            value.begin(),
            value.end(),
            [](const unsigned char ch) { return std::isdigit(ch) != 0; }))
    {
        throw InstallerError(
            ExitCode::InvalidManifest,
            "Manifest file size is not a strict decimal integer.");
    }
    std::uint64_t result = 0;
    for (const unsigned char ch : value)
    {
        const std::uint64_t digit = static_cast<std::uint64_t>(ch - '0');
        if (result > (UINT64_MAX - digit) / 10U)
        {
            throw InstallerError(
                ExitCode::InvalidManifest,
                "Manifest file size overflows uint64.");
        }
        result = result * 10U + digit;
    }
    if (result == 0)
    {
        throw InstallerError(
            ExitCode::InvalidManifest,
            "Manifest file size must be non-zero.");
    }
    return result;
}

[[nodiscard]] std::uint32_t ParseDecimalUint32(
    const std::string& value,
    const ExitCode code,
    const char* description)
{
    if (value.empty() || value.size() > 10U ||
        !std::all_of(
            value.begin(),
            value.end(),
            [](const unsigned char ch) { return std::isdigit(ch) != 0; }))
    {
        throw InstallerError(code, description);
    }
    std::uint64_t result = 0;
    for (const unsigned char ch : value)
    {
        result = result * 10U + static_cast<std::uint64_t>(ch - '0');
        if (result > UINT32_MAX)
        {
            throw InstallerError(code, description);
        }
    }
    return static_cast<std::uint32_t>(result);
}

void VerifyCompileTimeManifestPin(const std::string& actualHash)
{
    if (!comote::release_manifest_pin::kPinned)
    {
        throw InstallerError(
            ExitCode::InvalidManifest,
            "This installer was built without a release manifest pin.");
    }
    static_assert(
        std::size(comote::release_manifest_pin::kSha256Hex) == 65U,
        "Pinned release manifest SHA-256 must contain 64 hex characters.");
    volatile unsigned int difference = 0;
    for (size_t index = 0; index < 64U; ++index)
    {
        difference |= static_cast<unsigned int>(
            static_cast<unsigned char>(actualHash[index]) ^
            static_cast<unsigned char>(
                comote::release_manifest_pin::kSha256Hex[index]));
    }
    if (difference != 0)
    {
        throw InstallerError(
            ExitCode::InvalidManifest,
            "Manifest raw bytes do not match the compile-time release pin.");
    }
}
[[nodiscard]] std::string UpperAscii(std::string value)
{
    std::transform(
        value.begin(),
        value.end(),
        value.begin(),
        [](const unsigned char ch) {
            return static_cast<char>(std::toupper(ch));
        });
    return value;
}

[[noreturn]] void ThrowLastError(
    const ExitCode code,
    const std::string& operation)
{
    const DWORD error = GetLastError();
    throw InstallerError(
        code,
        operation + " failed with Win32 error " +
            std::to_string(error) + ".");
}

[[nodiscard]] fs::path FullPath(const fs::path& path)
{
    const DWORD required =
        GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    if (required == 0)
    {
        ThrowLastError(ExitCode::IoFailure, "GetFullPathNameW");
    }
    std::vector<wchar_t> buffer(static_cast<size_t>(required) + 1U);
    const DWORD written = GetFullPathNameW(
        path.c_str(),
        static_cast<DWORD>(buffer.size()),
        buffer.data(),
        nullptr);
    if (written == 0 || written >= static_cast<DWORD>(buffer.size()))
    {
        ThrowLastError(ExitCode::IoFailure, "GetFullPathNameW");
    }
    return fs::path(buffer.data()).lexically_normal();
}

[[nodiscard]] bool IsRegularNonReparseFile(const fs::path& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES &&
        (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0 &&
        (attributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0;
}

void RequireRegularNonReparseFile(
    const fs::path& path,
    const ExitCode code,
    const std::string& description)
{
    if (!IsRegularNonReparseFile(path))
    {
        throw InstallerError(
            code,
            description +
                " is missing, is not a regular file, or is a reparse point.");
    }
}

void RequireOrdinaryDirectory(
    const fs::path& path,
    const std::string& description)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES ||
        (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0 ||
        (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
    {
        throw InstallerError(
            ExitCode::IoFailure,
            description +
                " is missing, is not a directory, or is a reparse point.");
    }
}

struct LocalFileMetadata
{
    fs::path finalPath;
    std::uint64_t size{};
};

void RequireLocalFixedVolume(const fs::path& inputPath)
{
    const fs::path path = FullPath(inputPath);
    const std::wstring value = path.wstring();
    if (StartsWithInsensitive(value, L"\\\\") ||
        StartsWithInsensitive(value, L"\\?\\") ||
        StartsWithInsensitive(value, L"\\.\\"))
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "UNC and device-namespace paths are forbidden.");
    }
    wchar_t volumePath[MAX_PATH]{};
    if (!GetVolumePathNameW(path.c_str(), volumePath, MAX_PATH))
    {
        ThrowLastError(ExitCode::IoFailure, "GetVolumePathNameW");
    }
    if (GetDriveTypeW(volumePath) != DRIVE_FIXED)
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Package and state paths must be on a fixed local volume.");
    }
}

void RequireNoReparseAncestors(const fs::path& inputPath)
{
    fs::path current = FullPath(inputPath);
    for (;;)
    {
        const DWORD attributes = GetFileAttributesW(current.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES ||
            (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0 ||
            (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            throw InstallerError(
                ExitCode::IoFailure,
                "A path ancestor is missing, not a directory, or a reparse point.");
        }
        const fs::path parent = current.parent_path();
        if (parent.empty() || parent == current)
        {
            break;
        }
        current = parent;
    }
}

[[nodiscard]] fs::path FinalPathFromHandle(const HANDLE handle)
{
    const DWORD flags = FILE_NAME_NORMALIZED | VOLUME_NAME_DOS;
    const DWORD required = GetFinalPathNameByHandleW(
        handle,
        nullptr,
        0,
        flags);
    if (required == 0)
    {
        ThrowLastError(
            ExitCode::IoFailure,
            "GetFinalPathNameByHandleW(size)");
    }
    std::vector<wchar_t> value(static_cast<size_t>(required) + 1U);
    const DWORD written = GetFinalPathNameByHandleW(
        handle,
        value.data(),
        static_cast<DWORD>(value.size()),
        flags);
    if (written == 0 || written >= static_cast<DWORD>(value.size()))
    {
        ThrowLastError(
            ExitCode::IoFailure,
            "GetFinalPathNameByHandleW");
    }
    std::wstring path(value.data(), written);
    if (StartsWithInsensitive(path, L"\\\\?\\UNC\\"))
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Resolved UNC paths are forbidden.");
    }
    if (StartsWithInsensitive(path, L"\\\\?\\"))
    {
        path.erase(0, 4U);
    }
    return FullPath(fs::path(path));
}

[[nodiscard]] LocalFileMetadata InspectLocalRegularFile(
    const fs::path& inputPath,
    const std::optional<fs::path>& requiredParent)
{
    const fs::path path = FullPath(inputPath);
    RequireLocalFixedVolume(path);
    RequireNoReparseAncestors(path.parent_path());
    UniqueHandle file(CreateFileW(
        path.c_str(),
        GENERIC_READ | FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT,
        nullptr));
    if (!file || file.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(ExitCode::IoFailure, "CreateFileW(inspect)");
    }
    BY_HANDLE_FILE_INFORMATION information{};
    if (!GetFileInformationByHandle(file.get(), &information))
    {
        ThrowLastError(
            ExitCode::IoFailure,
            "GetFileInformationByHandle");
    }
    if ((information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ||
        (information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ||
        information.nNumberOfLinks != 1)
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Input must be a non-reparse regular file with one hard link.");
    }
    const fs::path finalPath = FinalPathFromHandle(file.get());
    if (!EqualsInsensitive(finalPath.wstring(), path.wstring()))
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Opened file final path differs from the requested path.");
    }
    if (requiredParent.has_value())
    {
        const fs::path parent = FullPath(requiredParent.value());
        if (!EqualsInsensitive(finalPath.parent_path().wstring(), parent.wstring()))
        {
            throw InstallerError(
                ExitCode::IoFailure,
                "Package file escaped the exact package directory.");
        }
    }
    const std::uint64_t size =
        (static_cast<std::uint64_t>(information.nFileSizeHigh) << 32U) |
        information.nFileSizeLow;
    if (size == 0)
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Package file must be non-empty.");
    }
    return LocalFileMetadata{finalPath, size};
}

void InspectLocalDirectory(const fs::path& inputPath)
{
    const fs::path path = FullPath(inputPath);
    RequireLocalFixedVolume(path);
    RequireNoReparseAncestors(path);
    UniqueHandle directory(CreateFileW(
        path.c_str(),
        FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
        nullptr));
    if (!directory || directory.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(ExitCode::IoFailure, "CreateFileW(directory)");
    }
    if (!EqualsInsensitive(
            FinalPathFromHandle(directory.get()).wstring(),
            path.wstring()))
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Directory final path differs from its requested path.");
    }
}

[[nodiscard]] bool IsExpectedProtectedAce(
    const ACCESS_ALLOWED_ACE& ace,
    const bool directory,
    PSID systemSid,
    PSID administratorsSid,
    bool& sawSystem,
    bool& sawAdministrators)
{
    const BYTE expectedFlags = directory
        ? static_cast<BYTE>(OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE)
        : 0;
    if (ace.Header.AceType != ACCESS_ALLOWED_ACE_TYPE ||
        ace.Header.AceFlags != expectedFlags ||
        ace.Mask != FILE_ALL_ACCESS)
    {
        return false;
    }
    PSID sid = const_cast<DWORD*>(&ace.SidStart);
    if (EqualSid(sid, systemSid) != FALSE && !sawSystem)
    {
        sawSystem = true;
        return true;
    }
    if (EqualSid(sid, administratorsSid) != FALSE &&
        !sawAdministrators)
    {
        sawAdministrators = true;
        return true;
    }
    return false;
}

void VerifyProtectedPathDacl(
    const fs::path& inputPath,
    const bool directory)
{
    const fs::path path = FullPath(inputPath);
    PSID owner = nullptr;
    PACL dacl = nullptr;
    PSECURITY_DESCRIPTOR rawDescriptor = nullptr;
    const DWORD securityResult = GetNamedSecurityInfoW(
        const_cast<wchar_t*>(path.c_str()),
        SE_FILE_OBJECT,
        OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        &owner,
        nullptr,
        &dacl,
        nullptr,
        &rawDescriptor);
    if (securityResult != ERROR_SUCCESS || rawDescriptor == nullptr ||
        owner == nullptr || dacl == nullptr)
    {
        SetLastError(securityResult);
        ThrowLastError(
            ExitCode::IoFailure,
            "GetNamedSecurityInfoW");
    }
    UniqueLocalMemory descriptor(
        static_cast<HLOCAL>(rawDescriptor));
    SECURITY_DESCRIPTOR_CONTROL control = 0;
    DWORD revision = 0;
    if (!GetSecurityDescriptorControl(
            rawDescriptor,
            &control,
            &revision) ||
        (control & SE_DACL_PROTECTED) == 0 ||
        dacl->AceCount != 2)
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Protected path DACL is not exact or protected.");
    }

    std::array<unsigned char, SECURITY_MAX_SID_SIZE> systemStorage{};
    std::array<unsigned char, SECURITY_MAX_SID_SIZE> adminStorage{};
    DWORD systemSize = static_cast<DWORD>(systemStorage.size());
    DWORD adminSize = static_cast<DWORD>(adminStorage.size());
    if (!CreateWellKnownSid(
            WinLocalSystemSid,
            nullptr,
            systemStorage.data(),
            &systemSize) ||
        !CreateWellKnownSid(
            WinBuiltinAdministratorsSid,
            nullptr,
            adminStorage.data(),
            &adminSize))
    {
        ThrowLastError(
            ExitCode::InternalError,
            "CreateWellKnownSid");
    }
    if (EqualSid(owner, adminStorage.data()) == FALSE)
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Protected path owner is not built-in Administrators.");
    }
    bool sawSystem = false;
    bool sawAdministrators = false;
    for (DWORD index = 0; index < dacl->AceCount; ++index)
    {
        void* rawAce = nullptr;
        if (!GetAce(dacl, index, &rawAce) || rawAce == nullptr)
        {
            ThrowLastError(ExitCode::IoFailure, "GetAce");
        }
        const auto& ace =
            *static_cast<const ACCESS_ALLOWED_ACE*>(rawAce);
        if (!IsExpectedProtectedAce(
                ace,
                directory,
                systemStorage.data(),
                adminStorage.data(),
                sawSystem,
                sawAdministrators))
        {
            throw InstallerError(
                ExitCode::IoFailure,
                "Protected path contains an unexpected access rule.");
        }
    }
    if (!sawSystem || !sawAdministrators)
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Protected path DACL principals are incomplete.");
    }
}

void ApplyAndVerifyProtectedPathDacl(
    const fs::path& inputPath,
    const bool directory)
{
    const wchar_t* sddl = directory
        ? L"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"
        : L"O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)";
    PSECURITY_DESCRIPTOR rawDescriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl,
            SDDL_REVISION_1,
            &rawDescriptor,
            nullptr) ||
        rawDescriptor == nullptr)
    {
        ThrowLastError(
            ExitCode::InternalError,
            "ConvertStringSecurityDescriptorToSecurityDescriptorW");
    }
    UniqueLocalMemory descriptor(
        static_cast<HLOCAL>(rawDescriptor));
    BOOL ownerDefaulted = FALSE;
    PSID owner = nullptr;
    if (!GetSecurityDescriptorOwner(
            rawDescriptor,
            &owner,
            &ownerDefaulted) || owner == nullptr)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "Unable to obtain the fixed protected owner.");
    }
    BOOL present = FALSE;
    BOOL defaulted = FALSE;
    PACL dacl = nullptr;
    if (!GetSecurityDescriptorDacl(
            rawDescriptor,
            &present,
            &dacl,
            &defaulted) ||
        present == FALSE || dacl == nullptr)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "Unable to obtain the fixed protected DACL.");
    }
    const DWORD result = SetNamedSecurityInfoW(
        const_cast<wchar_t*>(FullPath(inputPath).c_str()),
        SE_FILE_OBJECT,
        OWNER_SECURITY_INFORMATION |
            DACL_SECURITY_INFORMATION |
            PROTECTED_DACL_SECURITY_INFORMATION,
        owner,
        nullptr,
        dacl,
        nullptr);
    if (result != ERROR_SUCCESS)
    {
        SetLastError(result);
        ThrowLastError(
            ExitCode::IoFailure,
            "SetNamedSecurityInfoW");
    }
    VerifyProtectedPathDacl(inputPath, directory);
}

void EnsureProtectedDirectory(const fs::path& inputPath)
{
    const fs::path path = FullPath(inputPath);
    PSECURITY_DESCRIPTOR rawDescriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)",
            SDDL_REVISION_1,
            &rawDescriptor,
            nullptr) ||
        rawDescriptor == nullptr)
    {
        ThrowLastError(
            ExitCode::InternalError,
            "ConvertStringSecurityDescriptorToSecurityDescriptorW(directory)");
    }
    UniqueLocalMemory descriptor(
        static_cast<HLOCAL>(rawDescriptor));
    SECURITY_ATTRIBUTES attributes{};
    attributes.nLength = sizeof(attributes);
    attributes.lpSecurityDescriptor = rawDescriptor;
    attributes.bInheritHandle = FALSE;
    const BOOL created = CreateDirectoryW(path.c_str(), &attributes);
    if (!created)
    {
        const DWORD error = GetLastError();
        if (error != ERROR_ALREADY_EXISTS)
        {
            SetLastError(error);
            ThrowLastError(ExitCode::IoFailure, "CreateDirectoryW");
        }
        // Never adopt or repair a pre-created directory. Existing paths are
        // accepted only when their owner and protected DACL are already the
        // exact installer policy.
        InspectLocalDirectory(path);
        VerifyProtectedPathDacl(path, true);
        InspectLocalDirectory(path);
        return;
    }
    InspectLocalDirectory(path);
    VerifyProtectedPathDacl(path, true);
    InspectLocalDirectory(path);
}
void VerifyExactPackageFileSet(const fs::path& inputDirectory)
{
    const fs::path directory = FullPath(inputDirectory);
    const std::set<std::wstring> allowed = {
        kInfName,
        kCatName,
        kSysName};
    std::set<std::wstring> discovered;
    WIN32_FIND_DATAW data{};
    UniqueFindHandle search(FindFirstFileW(
        (directory / L"*").c_str(),
        &data));
    if (!search || search.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(
            ExitCode::PackageHashMismatch,
            "FindFirstFileW(package)");
    }
    do
    {
        const std::wstring name = data.cFileName;
        if (name == L"." || name == L"..")
        {
            continue;
        }
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ||
            (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ||
            allowed.find(name) == allowed.end() ||
            !discovered.insert(name).second)
        {
            throw InstallerError(
                ExitCode::PackageHashMismatch,
                "Package directory is not the exact canonical three-file set.");
        }
    } while (FindNextFileW(search.get(), &data));
    const DWORD finalError = GetLastError();
    if (finalError != ERROR_NO_MORE_FILES)
    {
        SetLastError(finalError);
        ThrowLastError(
            ExitCode::PackageHashMismatch,
            "FindNextFileW(package)");
    }
    if (discovered != allowed)
    {
        throw InstallerError(
            ExitCode::PackageHashMismatch,
            "Package directory is missing a canonical package file.");
    }
}

[[nodiscard]] std::string Sha256File(const fs::path& path)
{
    RequireRegularNonReparseFile(
        path,
        ExitCode::IoFailure,
        "Hash input");

    UniqueHandle file(CreateFileW(
        path.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr));
    if (!file || file.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(ExitCode::IoFailure, "CreateFileW");
    }

    BCRYPT_ALG_HANDLE rawAlgorithm = nullptr;
    if (BCryptOpenAlgorithmProvider(
            &rawAlgorithm,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            0) < 0)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "BCryptOpenAlgorithmProvider failed.");
    }
    UniqueBcryptAlgorithm algorithm(rawAlgorithm);

    DWORD objectLength = 0;
    DWORD copied = 0;
    if (BCryptGetProperty(
            algorithm.get(),
            BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&objectLength),
            sizeof(objectLength),
            &copied,
            0) < 0 ||
        copied != static_cast<DWORD>(sizeof(objectLength)))
    {
        throw InstallerError(
            ExitCode::InternalError,
            "BCryptGetProperty failed.");
    }

    std::vector<unsigned char> object(objectLength);
    BCRYPT_HASH_HANDLE rawHash = nullptr;
    if (BCryptCreateHash(
            algorithm.get(),
            &rawHash,
            object.data(),
            static_cast<ULONG>(object.size()),
            nullptr,
            0,
            0) < 0)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "BCryptCreateHash failed.");
    }
    UniqueBcryptHash hash(rawHash);

    std::array<unsigned char, 64U * 1024U> chunk{};
    for (;;)
    {
        DWORD bytesRead = 0;
        if (!ReadFile(
                file.get(),
                chunk.data(),
                static_cast<DWORD>(chunk.size()),
                &bytesRead,
                nullptr))
        {
            ThrowLastError(ExitCode::IoFailure, "ReadFile");
        }
        if (bytesRead == 0)
        {
            break;
        }
        if (BCryptHashData(
                hash.get(),
                chunk.data(),
                bytesRead,
                0) < 0)
        {
            throw InstallerError(
                ExitCode::InternalError,
                "BCryptHashData failed.");
        }
    }

    std::vector<unsigned char> digest(32U);
    if (BCryptFinishHash(
            hash.get(),
            digest.data(),
            static_cast<ULONG>(digest.size()),
            0) < 0)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "BCryptFinishHash failed.");
    }
    return Hex(digest);
}

[[nodiscard]] std::string Sha256Bytes(const std::string& bytes)
{
    BCRYPT_ALG_HANDLE rawAlgorithm = nullptr;
    if (BCryptOpenAlgorithmProvider(
            &rawAlgorithm,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            0) < 0)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "BCryptOpenAlgorithmProvider failed.");
    }
    UniqueBcryptAlgorithm algorithm(rawAlgorithm);

    BCRYPT_HASH_HANDLE rawHash = nullptr;
    if (BCryptCreateHash(
            algorithm.get(),
            &rawHash,
            nullptr,
            0,
            nullptr,
            0,
            BCRYPT_HASH_REUSABLE_FLAG) < 0)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "BCryptCreateHash failed.");
    }
    UniqueBcryptHash hash(rawHash);
    if (BCryptHashData(
            hash.get(),
            reinterpret_cast<PUCHAR>(
                const_cast<char*>(bytes.data())),
            static_cast<ULONG>(bytes.size()),
            0) < 0)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "BCryptHashData failed.");
    }
    std::vector<unsigned char> digest(32U);
    if (BCryptFinishHash(
            hash.get(),
            digest.data(),
            static_cast<ULONG>(digest.size()),
            0) < 0)
    {
        throw InstallerError(
            ExitCode::InternalError,
            "BCryptFinishHash failed.");
    }
    return Hex(digest);
}
[[nodiscard]] std::string ReadStrictAsciiFile(
    const fs::path& path,
    const ExitCode code,
    const size_t maximumBytes)
{
    RequireRegularNonReparseFile(path, code, "Text input");
    std::ifstream stream(path, std::ios::binary);
    if (!stream)
    {
        throw InstallerError(code, "Unable to open text input.");
    }
    std::string content{
        std::istreambuf_iterator<char>{stream},
        std::istreambuf_iterator<char>{}};
    if (stream.bad() || content.empty() || content.size() > maximumBytes)
    {
        throw InstallerError(code, "Text input has an invalid size.");
    }
    for (const unsigned char ch : content)
    {
        if (ch == 0 || ch > 0x7f)
        {
            throw InstallerError(code, "Text input must be strict ASCII.");
        }
    }
    return content;
}
[[nodiscard]] std::map<std::string, std::string> ParseStrictDocument(
    const std::string& content,
    const std::string& expectedHeader,
    const std::set<std::string>& expectedKeys,
    const ExitCode code)
{
    std::istringstream stream(content);
    std::string line;
    if (!std::getline(stream, line))
    {
        throw InstallerError(code, "Document header is missing.");
    }
    if (!line.empty() && line.back() == '\r')
    {
        line.pop_back();
    }
    if (line != expectedHeader)
    {
        throw InstallerError(code, "Document header is invalid.");
    }

    std::map<std::string, std::string> values;
    while (std::getline(stream, line))
    {
        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }
        if (line.empty())
        {
            continue;
        }
        const size_t separator = line.find('=');
        if (separator == std::string::npos ||
            separator == 0 ||
            separator == line.size() - 1 ||
            line.find('=', separator + 1) != std::string::npos)
        {
            throw InstallerError(code, "Document line syntax is invalid.");
        }
        const std::string key = line.substr(0, separator);
        const std::string value = line.substr(separator + 1);
        if (expectedKeys.find(key) == expectedKeys.end() ||
            values.find(key) != values.end())
        {
            throw InstallerError(
                code,
                "Document contains an unknown or duplicate key.");
        }
        values.emplace(key, value);
    }
    if (values.size() != expectedKeys.size())
    {
        throw InstallerError(code, "Document is missing a required key.");
    }
    return values;
}

[[nodiscard]] Manifest LoadManifest(const fs::path& inputPath)
{
    const fs::path path = FullPath(inputPath);
    (void)InspectLocalRegularFile(path, std::nullopt);
    const std::string content = ReadStrictAsciiFile(
        path,
        ExitCode::InvalidManifest,
        16U * 1024U);
    const std::string manifestHash = Sha256Bytes(content);
    VerifyCompileTimeManifestPin(manifestHash);
    const std::map<std::string, std::string> values =
        ParseStrictDocument(
            content,
            kManifestHeader,
            {
                "HardwareId",
                "RootInstanceId",
                "ServiceName",
                "Provider",
                "PackageFiles",
                "InfSize",
                "InfSha256",
                "CatSize",
                "CatSha256",
                "SysSize",
                "SysSha256"
            },
            ExitCode::InvalidManifest);

    if (values.at("PackageFiles") !=
        "ComoteVirtualHidPhase2.inf,ComoteVirtualHidPhase2.cat,ComoteVirtualHidPhase2.sys")
    {
        throw InstallerError(
            ExitCode::InvalidManifest,
            "Manifest package file policy is not the exact Phase 2 set.");
    }

    Manifest manifest{
        WidenAscii(values.at("HardwareId")),
        WidenAscii(values.at("RootInstanceId")),
        WidenAscii(values.at("ServiceName")),
        WidenAscii(values.at("Provider")),
        UpperAscii(values.at("InfSha256")),
        UpperAscii(values.at("CatSha256")),
        UpperAscii(values.at("SysSha256")),
        manifestHash,
        ParseDecimalSize(values.at("InfSize")),
        ParseDecimalSize(values.at("CatSize")),
        ParseDecimalSize(values.at("SysSize"))};

    if (!EqualsInsensitive(manifest.hardwareId, kHardwareId) ||
        !EqualsInsensitive(manifest.rootInstanceId, kRootInstanceId) ||
        !EqualsInsensitive(manifest.serviceName, kServiceName) ||
        !EqualsInsensitive(manifest.provider, kProvider) ||
        !IsSha256(manifest.infSha256) ||
        !IsSha256(manifest.catSha256) ||
        !IsSha256(manifest.sysSha256))
    {
        throw InstallerError(
            ExitCode::InvalidManifest,
            "Manifest identity or SHA-256 value is invalid.");
    }
    return manifest;
}
[[nodiscard]] std::string SerializeState(const InstallerState& state)
{
    std::ostringstream stream;
    stream << kStateHeader << "\r\n"
           << "Status=" << state.status << "\r\n"
           << "Operation=" << state.operation << "\r\n"
           << "ManifestSha256=" << state.manifestSha256 << "\r\n"
           << "HardwareId=" << NarrowAscii(state.hardwareId) << "\r\n"
           << "RootInstanceId=" << NarrowAscii(state.rootInstanceId)
           << "\r\n"
           << "ServiceName=" << NarrowAscii(state.serviceName) << "\r\n"
           << "PublishedInf=" << NarrowAscii(state.publishedInf) << "\r\n"
           << "StagePath=" << NarrowAscii(state.stagePath) << "\r\n"
           << "InfSha256=" << state.infSha256 << "\r\n"
           << "CatSha256=" << state.catSha256 << "\r\n"
           << "SysSha256=" << state.sysSha256 << "\r\n"
           << "BootId=" << state.bootId << "\r\n"
           << "NeedsReboot=" << (state.needsReboot ? "1" : "0")
           << "\r\n";
    return stream.str();
}

[[nodiscard]] InstallerState ParseState(const std::string& content)
{
    const std::map<std::string, std::string> values =
        ParseStrictDocument(
            content,
            kStateHeader,
            {
                "Status",
                "Operation",
                "ManifestSha256",
                "HardwareId",
                "RootInstanceId",
                "ServiceName",
                "PublishedInf",
                "StagePath",
                "InfSha256",
                "CatSha256",
                "SysSha256",
                "BootId",
                "NeedsReboot"
            },
            ExitCode::RecoveryRequired);
    InstallerState state{
        values.at("Status"),
        values.at("Operation"),
        UpperAscii(values.at("ManifestSha256")),
        WidenAscii(values.at("HardwareId")),
        WidenAscii(values.at("RootInstanceId")),
        WidenAscii(values.at("ServiceName")),
        WidenAscii(values.at("PublishedInf")),
        WidenAscii(values.at("StagePath")),
        UpperAscii(values.at("InfSha256")),
        UpperAscii(values.at("CatSha256")),
        UpperAscii(values.at("SysSha256")),
        ParseDecimalUint32(
            values.at("BootId"),
            ExitCode::RecoveryRequired,
            "Installer state BootId is invalid."),
        values.at("NeedsReboot") == "1"};

    const bool validStatus =
        state.status == "Installing" ||
        state.status == "Installed" ||
        state.status == "Removing" ||
        state.status == "RecoveryRequired";
    const bool validOperation =
        state.operation == "Install" || state.operation == "Remove";
    const bool validInf =
        state.publishedInf == L"-" ||
        (StartsWithInsensitive(state.publishedInf, L"oem") &&
         EndsWithInsensitive(state.publishedInf, L".inf") &&
         state.publishedInf.size() > 7);
    const bool validStage =
        state.stagePath == L"-" || !state.stagePath.empty();
    const std::string& rebootValue = values.at("NeedsReboot");
    if (!validStatus || !validOperation || !validInf || !validStage ||
        (rebootValue != "0" && rebootValue != "1") ||
        !EqualsInsensitive(state.hardwareId, kHardwareId) ||
        !EqualsInsensitive(state.rootInstanceId, kRootInstanceId) ||
        !EqualsInsensitive(state.serviceName, kServiceName) ||
        !IsSha256(state.manifestSha256) ||
        !IsSha256(state.infSha256) ||
        !IsSha256(state.catSha256) ||
        !IsSha256(state.sysSha256))
    {
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Installer state is invalid.");
    }
    return state;
}
[[nodiscard]] fs::path ProgramDataDirectory()
{
    PWSTR rawPath = nullptr;
    const HRESULT result = SHGetKnownFolderPath(
        FOLDERID_ProgramData,
        KF_FLAG_DEFAULT,
        nullptr,
        &rawPath);
    if (FAILED(result) || rawPath == nullptr)
    {
        throw InstallerError(
            ExitCode::IoFailure,
            "Unable to resolve ProgramData.");
    }
    const fs::path path = FullPath(fs::path(rawPath));
    CoTaskMemFree(rawPath);
    RequireLocalFixedVolume(path);
    InspectLocalDirectory(path);
    return path;
}

[[nodiscard]] std::uint32_t CurrentBootId()
{
    constexpr wchar_t key[] =
        L"SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Memory Management\\PrefetchParameters";
    constexpr wchar_t valueName[] = L"BootId";
    DWORD value = 0;
    DWORD size = sizeof(value);
    const LSTATUS result = RegGetValueW(
        HKEY_LOCAL_MACHINE,
        key,
        valueName,
        RRF_RT_REG_DWORD,
        nullptr,
        &value,
        &size);
    if (result != ERROR_SUCCESS || size != sizeof(value))
    {
        SetLastError(static_cast<DWORD>(result));
        ThrowLastError(ExitCode::IoFailure, "RegGetValueW(BootId)");
    }
    return static_cast<std::uint32_t>(value);
}

[[nodiscard]] fs::path DriverDataDirectory()
{
    return ProgramDataDirectory() / L"ComoteDriverInstaller" / L"Phase2";
}

[[nodiscard]] fs::path DefaultStagingDirectory()
{
    return DriverDataDirectory() / L"Staging" / L"Phase2";
}

void EnsureProtectedDriverTree()
{
    const fs::path base = ProgramDataDirectory();
    const fs::path installer = base / L"ComoteDriverInstaller";
    const fs::path phase2 = installer / L"Phase2";
    EnsureProtectedDirectory(installer);
    EnsureProtectedDirectory(phase2);
}

void VerifyProtectedDriverTree()
{
    const fs::path base = ProgramDataDirectory();
    const fs::path installer = base / L"ComoteDriverInstaller";
    const fs::path phase2 = installer / L"Phase2";
    InspectLocalDirectory(installer);
    VerifyProtectedPathDacl(installer, true);
    InspectLocalDirectory(phase2);
    VerifyProtectedPathDacl(phase2, true);
}

[[nodiscard]] fs::path EnsureProtectedStagingDirectory()
{
    EnsureProtectedDriverTree();
    const fs::path staging = DriverDataDirectory() / L"Staging";
    const fs::path phase2 = staging / L"Phase2";
    EnsureProtectedDirectory(staging);
    EnsureProtectedDirectory(phase2);
    return phase2;
}

[[nodiscard]] fs::path DefaultStatePath()
{
    return DriverDataDirectory() / L"phase2-installer.state";
}

[[nodiscard]] std::optional<InstallerState> LoadState(
    const fs::path& inputPath)
{
    const fs::path path = FullPath(inputPath);
    if (!fs::exists(path))
    {
        return std::nullopt;
    }
    (void)InspectLocalRegularFile(path, path.parent_path());
    if (EqualsInsensitive(
            path.parent_path().wstring(),
            FullPath(DriverDataDirectory()).wstring()))
    {
        VerifyProtectedDriverTree();
    }
#if COMOTE_INSTALLER_VM_TEST
    else
    {
        VerifyProtectedPathDacl(path.parent_path(), true);
    }
#endif
    VerifyProtectedPathDacl(path, false);
    return ParseState(ReadStrictAsciiFile(
        path,
        ExitCode::RecoveryRequired,
        16U * 1024U));
}
void WriteStateAtomically(
    const fs::path& inputPath,
    const InstallerState& state)
{
    const fs::path path = FullPath(inputPath);
    const fs::path parent = path.parent_path();
    const fs::path productionParent = FullPath(DriverDataDirectory());
    if (EqualsInsensitive(parent.wstring(), productionParent.wstring()))
    {
        EnsureProtectedDriverTree();
    }
    else
    {
#if COMOTE_INSTALLER_VM_TEST
        InspectLocalDirectory(parent);
        VerifyProtectedPathDacl(parent, true);
#else
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Production installer state path is not fixed.");
#endif
    }
    if (fs::exists(path))
    {
        (void)InspectLocalRegularFile(path, parent);
        VerifyProtectedPathDacl(path, false);
    }

    const fs::path temporary =
        path.wstring() + L".tmp." +
        std::to_wstring(GetCurrentProcessId()) + L"." +
        std::to_wstring(GetTickCount64());
    const std::string content = SerializeState(state);
    UniqueHandle file(CreateFileW(
        temporary.c_str(),
        GENERIC_WRITE | FILE_READ_ATTRIBUTES,
        0,
        nullptr,
        CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH,
        nullptr));
    if (!file || file.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(ExitCode::IoFailure, "Creating temporary state");
    }
    DWORD written = 0;
    if (!WriteFile(
            file.get(),
            content.data(),
            static_cast<DWORD>(content.size()),
            &written,
            nullptr) ||
        written != static_cast<DWORD>(content.size()) ||
        !FlushFileBuffers(file.get()))
    {
        file.reset();
        (void)DeleteFileW(temporary.c_str());
        ThrowLastError(ExitCode::IoFailure, "Writing installer state");
    }
    file.reset();
    (void)InspectLocalRegularFile(temporary, parent);
    ApplyAndVerifyProtectedPathDacl(temporary, false);
    if (!MoveFileExW(
            temporary.c_str(),
            path.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        (void)DeleteFileW(temporary.c_str());
        ThrowLastError(ExitCode::IoFailure, "Committing installer state");
    }
    (void)InspectLocalRegularFile(path, parent);
    ApplyAndVerifyProtectedPathDacl(path, false);
}
void DeleteState(const fs::path& inputPath)
{
    const fs::path path = FullPath(inputPath);
    if (!fs::exists(path))
    {
        return;
    }
    (void)InspectLocalRegularFile(path, path.parent_path());
    VerifyProtectedPathDacl(path, false);
    if (!DeleteFileW(path.c_str()))
    {
        ThrowLastError(ExitCode::IoFailure, "Deleting installer state");
    }
}
[[nodiscard]] std::wstring QueryInfVersionValue(
    const fs::path& infPath,
    const wchar_t* key)
{
    DWORD required = 0;
    (void)SetupGetInfInformationW(
        infPath.c_str(),
        INFINFO_INF_NAME_IS_ABSOLUTE,
        nullptr,
        0,
        &required);
    if (required == 0)
    {
        ThrowLastError(
            ExitCode::InfIdentityInvalid,
            "SetupGetInfInformationW");
    }
    std::vector<unsigned char> storage(required);
    auto* information =
        reinterpret_cast<PSP_INF_INFORMATION>(storage.data());
    if (!SetupGetInfInformationW(
            infPath.c_str(),
            INFINFO_INF_NAME_IS_ABSOLUTE,
            information,
            required,
            nullptr))
    {
        ThrowLastError(
            ExitCode::InfIdentityInvalid,
            "SetupGetInfInformationW");
    }

    DWORD valueLength = 0;
    (void)SetupQueryInfVersionInformationW(
        information,
        0,
        key,
        nullptr,
        0,
        &valueLength);
    if (valueLength == 0)
    {
        ThrowLastError(
            ExitCode::InfIdentityInvalid,
            "SetupQueryInfVersionInformationW");
    }
    std::vector<wchar_t> value(valueLength);
    if (!SetupQueryInfVersionInformationW(
            information,
            0,
            key,
            value.data(),
            static_cast<DWORD>(value.size()),
            nullptr))
    {
        ThrowLastError(
            ExitCode::InfIdentityInvalid,
            "SetupQueryInfVersionInformationW");
    }
    return value.data();
}
[[noreturn]] void ThrowInfIdentity(const std::string& message)
{
    throw InstallerError(ExitCode::InfIdentityInvalid, message);
}

[[nodiscard]] std::optional<std::wstring> TryGetInfStringField(
    const INFCONTEXT& inputContext,
    const DWORD fieldIndex)
{
    INFCONTEXT context = inputContext;
    DWORD required = 0;
    if (!SetupGetStringFieldW(
            &context,
            fieldIndex,
            nullptr,
            0,
            &required))
    {
        return std::nullopt;
    }
    if (required == 0)
    {
        ThrowInfIdentity("INF field reported an invalid size.");
    }
    std::vector<wchar_t> value(required);
    if (!SetupGetStringFieldW(
            &context,
            fieldIndex,
            value.data(),
            static_cast<DWORD>(value.size()),
            nullptr))
    {
        ThrowInfIdentity("INF string field could not be read.");
    }
    return std::wstring(value.data());
}

[[nodiscard]] std::wstring GetInfStringField(
    const INFCONTEXT& context,
    const DWORD fieldIndex)
{
    const std::optional<std::wstring> value =
        TryGetInfStringField(context, fieldIndex);
    if (!value.has_value())
    {
        ThrowInfIdentity("A required INF string field is missing.");
    }
    return value.value();
}

[[nodiscard]] int GetInfIntField(
    const INFCONTEXT& inputContext,
    const DWORD fieldIndex)
{
    INFCONTEXT context = inputContext;
    int value = 0;
    if (!SetupGetIntField(&context, fieldIndex, &value))
    {
        ThrowInfIdentity("A required INF integer field is invalid.");
    }
    return value;
}

[[nodiscard]] std::vector<INFCONTEXT> GetInfSectionLines(
    const HINF inf,
    const std::wstring& section,
    const size_t expectedCount)
{
    const LONG count = SetupGetLineCountW(inf, section.c_str());
    if (count < 0 ||
        static_cast<size_t>(count) != expectedCount ||
        expectedCount == 0)
    {
        ThrowInfIdentity("INF section has an unexpected line count.");
    }

    INFCONTEXT context{};
    if (!SetupFindFirstLineW(
            inf,
            section.c_str(),
            nullptr,
            &context))
    {
        ThrowInfIdentity("Required INF section could not be opened.");
    }

    std::vector<INFCONTEXT> lines;
    lines.reserve(expectedCount);
    lines.push_back(context);
    for (size_t index = 1; index < expectedCount; ++index)
    {
        INFCONTEXT next{};
        if (!SetupFindNextLine(&context, &next))
        {
            ThrowInfIdentity(
                "INF section ended before its declared line count.");
        }
        lines.push_back(next);
        context = next;
    }
    INFCONTEXT unexpected{};
    if (SetupFindNextLine(&context, &unexpected))
    {
        ThrowInfIdentity("INF section contains an unexpected extra line.");
    }
    return lines;
}

void RequireInfFieldCount(
    const INFCONTEXT& inputContext,
    const DWORD expectedCount)
{
    INFCONTEXT context = inputContext;
    if (SetupGetFieldCount(&context) != expectedCount)
    {
        ThrowInfIdentity("INF line has an unexpected field count.");
    }
}

[[nodiscard]] const INFCONTEXT& RequireUniqueInfKey(
    const std::vector<INFCONTEXT>& lines,
    const std::wstring& expectedKey)
{
    const INFCONTEXT* match = nullptr;
    for (const INFCONTEXT& line : lines)
    {
        const std::optional<std::wstring> key =
            TryGetInfStringField(line, 0);
        if (key.has_value() &&
            EqualsInsensitive(key.value(), expectedKey))
        {
            if (match != nullptr)
            {
                ThrowInfIdentity("INF contains a duplicate directive.");
            }
            match = &line;
        }
    }
    if (match == nullptr)
    {
        ThrowInfIdentity("INF is missing a required directive.");
    }
    return *match;
}

void RequireInfStringLine(
    const INFCONTEXT& line,
    const std::wstring& expectedKey,
    const std::vector<std::wstring>& expectedFields)
{
    RequireInfFieldCount(
        line,
        static_cast<DWORD>(expectedFields.size()));
    if (!EqualsInsensitive(GetInfStringField(line, 0), expectedKey))
    {
        ThrowInfIdentity("INF directive key does not match.");
    }
    for (size_t index = 0; index < expectedFields.size(); ++index)
    {
        if (!EqualsInsensitive(
                GetInfStringField(
                    line,
                    static_cast<DWORD>(index + 1)),
                expectedFields[index]))
        {
            ThrowInfIdentity("INF directive value does not match.");
        }
    }
}

void RequireInfIntLine(
    const INFCONTEXT& line,
    const std::wstring& expectedKey,
    const int expectedValue)
{
    RequireInfFieldCount(line, 1);
    if (!EqualsInsensitive(GetInfStringField(line, 0), expectedKey) ||
        GetInfIntField(line, 1) != expectedValue)
    {
        ThrowInfIdentity("INF integer directive does not match.");
    }
}

[[nodiscard]] std::wstring GetActualModelsSection(
    const INFCONTEXT& inputContext)
{
    INFCONTEXT context = inputContext;
    DWORD required = 0;
    if (!SetupDiGetActualModelsSectionW(
            &context,
            nullptr,
            nullptr,
            0,
            &required,
            nullptr) ||
        required == 0)
    {
        ThrowInfIdentity("INF model decoration could not be selected.");
    }
    std::vector<wchar_t> section(required);
    if (!SetupDiGetActualModelsSectionW(
            &context,
            nullptr,
            section.data(),
            static_cast<DWORD>(section.size()),
            nullptr,
            nullptr))
    {
        ThrowInfIdentity("Selected INF model section could not be read.");
    }
    return section.data();
}

[[nodiscard]] std::wstring GetActualInstallSection(
    const HINF inf,
    const std::wstring& baseSection)
{
    DWORD required = 0;
    if (!SetupDiGetActualSectionToInstallW(
            inf,
            baseSection.c_str(),
            nullptr,
            0,
            &required,
            nullptr) ||
        required == 0)
    {
        ThrowInfIdentity("INF install decoration could not be selected.");
    }
    std::vector<wchar_t> section(required);
    if (!SetupDiGetActualSectionToInstallW(
            inf,
            baseSection.c_str(),
            section.data(),
            static_cast<DWORD>(section.size()),
            nullptr,
            nullptr))
    {
        ThrowInfIdentity("Selected INF install section could not be read.");
    }
    return section.data();
}

[[nodiscard]] std::wstring FoldInfName(std::wstring value)
{
    std::transform(
        value.begin(),
        value.end(),
        value.begin(),
        [](const wchar_t ch) {
            return static_cast<wchar_t>(std::towlower(ch));
        });
    return value;
}

void RequireExactInfSections(
    const HINF inf,
    const std::set<std::wstring>& expectedSections)
{
    std::set<std::wstring> actualSections;
    for (UINT index = 0;; ++index)
    {
        UINT required = 0;
        const BOOL sized = SetupEnumInfSectionsW(
            inf,
            index,
            nullptr,
            0,
            &required);
        if (!sized)
        {
            const DWORD error = GetLastError();
            if (error == ERROR_NO_MORE_ITEMS)
            {
                break;
            }
            if (error != ERROR_INSUFFICIENT_BUFFER || required == 0)
            {
                ThrowInfIdentity("INF section enumeration failed.");
            }
        }
        if (required == 0)
        {
            ThrowInfIdentity("INF section reported an invalid name size.");
        }
        std::vector<wchar_t> section(required);
        if (!SetupEnumInfSectionsW(
                inf,
                index,
                section.data(),
                static_cast<UINT>(section.size()),
                nullptr))
        {
            ThrowInfIdentity("INF section name could not be read.");
        }
        if (!actualSections.insert(FoldInfName(section.data())).second)
        {
            ThrowInfIdentity("INF contains a duplicate section.");
        }
    }

    std::set<std::wstring> foldedExpected;
    for (const std::wstring& section : expectedSections)
    {
        foldedExpected.insert(FoldInfName(section));
    }
    if (actualSections != foldedExpected)
    {
        ThrowInfIdentity("INF section set does not match Phase 2.");
    }
}

void VerifyInfIdentity(const fs::path& infPath)
{
    UINT errorLine = 0;
    UniqueInfHandle inf(SetupOpenInfFileW(
        infPath.c_str(),
        nullptr,
        INF_STYLE_WIN4,
        &errorLine));
    if (!inf || inf.get() == INVALID_HANDLE_VALUE)
    {
        ThrowInfIdentity(
            "INF syntax is invalid at line " +
            std::to_string(errorLine) + ".");
    }

    const std::wstring modelSection =
        L"ComoteModels.NTamd64.10.0...19045";
    const std::wstring installBase =
        L"ComoteVirtualHidPhase2_Install";
    const std::wstring installSection = installBase + L".NT";
    const std::wstring copySection = L"ComoteDriverCopy";
    const std::wstring filterSection =
        L"ComoteVirtualHidPhase2_VhfLowerFilter";
    const std::wstring serviceSection =
        L"ComoteVirtualHidPhase2_Service";
    const std::wstring wdfSection = L"ComoteVirtualHidPhase2_Wdf";

    const std::vector<INFCONTEXT> manufacturer =
        GetInfSectionLines(inf.get(), L"Manufacturer", 1);
    RequireInfStringLine(
        manufacturer.front(),
        kProvider,
        {L"ComoteModels", L"NTamd64.10.0...19045"});
    if (!EqualsInsensitive(
            GetActualModelsSection(manufacturer.front()),
            modelSection))
    {
        ThrowInfIdentity("INF selected an unexpected models section.");
    }

    const std::vector<INFCONTEXT> models =
        GetInfSectionLines(inf.get(), modelSection, 1);
    RequireInfStringLine(
        models.front(),
        L"Comote Virtual HID Phase 2 Source",
        {installBase, kHardwareId});
    if (!EqualsInsensitive(
            GetActualInstallSection(inf.get(), installBase),
            installSection))
    {
        ThrowInfIdentity("INF selected an unexpected install section.");
    }

    const std::vector<INFCONTEXT> install =
        GetInfSectionLines(inf.get(), installSection, 1);
    RequireInfStringLine(
        install.front(),
        L"CopyFiles",
        {copySection});

    const std::vector<INFCONTEXT> copy =
        GetInfSectionLines(inf.get(), copySection, 1);
    RequireInfFieldCount(copy.front(), 1);
    if (!EqualsInsensitive(
            GetInfStringField(copy.front(), 1),
            kSysName))
    {
        ThrowInfIdentity("INF CopyFiles entry does not match.");
    }

    const std::vector<INFCONTEXT> hardware =
        GetInfSectionLines(inf.get(), installSection + L".HW", 1);
    RequireInfStringLine(
        hardware.front(),
        L"AddReg",
        {filterSection});

    const std::vector<INFCONTEXT> filter =
        GetInfSectionLines(inf.get(), filterSection, 1);
    RequireInfFieldCount(filter.front(), 5);
    if (TryGetInfStringField(filter.front(), 0).has_value() ||
        !EqualsInsensitive(GetInfStringField(filter.front(), 1), L"HKR") ||
        !GetInfStringField(filter.front(), 2).empty() ||
        !EqualsInsensitive(
            GetInfStringField(filter.front(), 3),
            L"LowerFilters") ||
        GetInfIntField(filter.front(), 4) != 0x00010000 ||
        !EqualsInsensitive(GetInfStringField(filter.front(), 5), L"vhf"))
    {
        ThrowInfIdentity("INF VHF lower-filter entry does not match.");
    }

    const std::vector<INFCONTEXT> services =
        GetInfSectionLines(
            inf.get(),
            installSection + L".Services",
            1);
    RequireInfFieldCount(services.front(), 3);
    if (!EqualsInsensitive(
            GetInfStringField(services.front(), 0),
            L"AddService") ||
        !EqualsInsensitive(
            GetInfStringField(services.front(), 1),
            kServiceName) ||
        GetInfIntField(services.front(), 2) != 0x00000002 ||
        !EqualsInsensitive(
            GetInfStringField(services.front(), 3),
            serviceSection))
    {
        ThrowInfIdentity("INF AddService entry does not match.");
    }

    const std::vector<INFCONTEXT> service =
        GetInfSectionLines(inf.get(), serviceSection, 5);
    RequireInfStringLine(
        RequireUniqueInfKey(service, L"DisplayName"),
        L"DisplayName",
        {L"Comote Virtual HID Phase 2 Source Driver"});
    RequireInfIntLine(
        RequireUniqueInfKey(service, L"ServiceType"),
        L"ServiceType",
        1);
    RequireInfIntLine(
        RequireUniqueInfKey(service, L"StartType"),
        L"StartType",
        3);
    RequireInfIntLine(
        RequireUniqueInfKey(service, L"ErrorControl"),
        L"ErrorControl",
        1);
    RequireInfStringLine(
        RequireUniqueInfKey(service, L"ServiceBinary"),
        L"ServiceBinary",
        {std::wstring(L".\\") + kSysName});

    const std::vector<INFCONTEXT> wdf =
        GetInfSectionLines(inf.get(), installSection + L".Wdf", 1);
    RequireInfStringLine(
        wdf.front(),
        L"KmdfService",
        {kServiceName, wdfSection});
    const std::vector<INFCONTEXT> wdfInstall =
        GetInfSectionLines(inf.get(), wdfSection, 1);
    RequireInfStringLine(
        wdfInstall.front(),
        L"KmdfLibraryVersion",
        {L"1.15"});

    const std::vector<INFCONTEXT> sourceNames =
        GetInfSectionLines(inf.get(), L"SourceDisksNames", 1);
    RequireInfFieldCount(sourceNames.front(), 4);
    if (GetInfIntField(sourceNames.front(), 0) != 1 ||
        !EqualsInsensitive(
            GetInfStringField(sourceNames.front(), 1),
            L"Comote Virtual HID Phase 2 Installation Media") ||
        !GetInfStringField(sourceNames.front(), 2).empty() ||
        !GetInfStringField(sourceNames.front(), 3).empty() ||
        !GetInfStringField(sourceNames.front(), 4).empty())
    {
        ThrowInfIdentity("INF source disk entry does not match.");
    }

    const std::vector<INFCONTEXT> sourceFiles =
        GetInfSectionLines(inf.get(), L"SourceDisksFiles", 1);
    RequireInfFieldCount(sourceFiles.front(), 1);
    if (!EqualsInsensitive(
            GetInfStringField(sourceFiles.front(), 0),
            kSysName) ||
        GetInfIntField(sourceFiles.front(), 1) != 1)
    {
        ThrowInfIdentity("INF source file entry does not match.");
    }

    const std::vector<INFCONTEXT> destinations =
        GetInfSectionLines(inf.get(), L"DestinationDirs", 2);
    RequireInfIntLine(
        RequireUniqueInfKey(destinations, L"DefaultDestDir"),
        L"DefaultDestDir",
        13);
    RequireInfIntLine(
        RequireUniqueInfKey(destinations, copySection),
        copySection,
        13);

    const std::vector<INFCONTEXT> version =
        GetInfSectionLines(inf.get(), L"Version", 7);
    RequireInfStringLine(
        RequireUniqueInfKey(version, L"Signature"),
        L"Signature",
        {L"$WINDOWS NT$"});
    RequireInfStringLine(
        RequireUniqueInfKey(version, L"Class"),
        L"Class",
        {L"System"});
    RequireInfStringLine(
        RequireUniqueInfKey(version, L"ClassGuid"),
        L"ClassGuid",
        {L"{4d36e97d-e325-11ce-bfc1-08002be10318}"});
    RequireInfStringLine(
        RequireUniqueInfKey(version, L"Provider"),
        L"Provider",
        {kProvider});
    RequireInfStringLine(
        RequireUniqueInfKey(version, L"DriverVer"),
        L"DriverVer",
        {L"07/30/2026", L"0.2.0.0"});
    RequireInfStringLine(
        RequireUniqueInfKey(version, L"CatalogFile"),
        L"CatalogFile",
        {kCatName});
    RequireInfIntLine(
        RequireUniqueInfKey(version, L"PnpLockdown"),
        L"PnpLockdown",
        1);

    const std::vector<INFCONTEXT> strings =
        GetInfSectionLines(inf.get(), L"Strings", 4);
    RequireInfStringLine(
        RequireUniqueInfKey(strings, L"ProviderName"),
        L"ProviderName",
        {kProvider});
    RequireInfStringLine(
        RequireUniqueInfKey(strings, L"DeviceDescription"),
        L"DeviceDescription",
        {L"Comote Virtual HID Phase 2 Source"});
    RequireInfStringLine(
        RequireUniqueInfKey(strings, L"ServiceDescription"),
        L"ServiceDescription",
        {L"Comote Virtual HID Phase 2 Source Driver"});
    RequireInfStringLine(
        RequireUniqueInfKey(strings, L"DiskName"),
        L"DiskName",
        {L"Comote Virtual HID Phase 2 Installation Media"});

    RequireExactInfSections(
        inf.get(),
        {
            L"Version",
            L"DestinationDirs",
            L"SourceDisksNames",
            L"SourceDisksFiles",
            L"Manufacturer",
            modelSection,
            installSection,
            copySection,
            installSection + L".HW",
            filterSection,
            installSection + L".Services",
            serviceSection,
            installSection + L".Wdf",
            wdfSection,
            L"Strings"
        });
}
void VerifyAuthenticodeFile(const fs::path& path)
{
    std::wstring mutablePath = path.wstring();
    WINTRUST_FILE_INFO fileInfo{};
    fileInfo.cbStruct = sizeof(fileInfo);
    fileInfo.pcwszFilePath = mutablePath.c_str();

    WINTRUST_DATA trustData{};
    trustData.cbStruct = sizeof(trustData);
    trustData.dwUIChoice = WTD_UI_NONE;
    trustData.fdwRevocationChecks = WTD_REVOKE_NONE;
    trustData.dwUnionChoice = WTD_CHOICE_FILE;
    trustData.pFile = &fileInfo;
    trustData.dwStateAction = WTD_STATEACTION_VERIFY;
    trustData.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;

    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    const LONG result = WinVerifyTrust(
        nullptr,
        &action,
        &trustData);
    trustData.dwStateAction = WTD_STATEACTION_CLOSE;
    (void)WinVerifyTrust(nullptr, &action, &trustData);
    if (result != ERROR_SUCCESS)
    {
        throw InstallerError(
            ExitCode::SignatureInvalid,
            "Authenticode verification failed with status " +
                std::to_string(result) + ".");
    }
}

using AcquireCatalogContext2Function = BOOL(WINAPI*)(
    HCATADMIN*,
    const GUID*,
    PCWSTR,
    PCCERT_STRONG_SIGN_PARA,
    DWORD);
using CalculateCatalogHash2Function = BOOL(WINAPI*)(
    HCATADMIN,
    HANDLE,
    DWORD*,
    BYTE*,
    DWORD);

[[nodiscard]] FARPROC RequiredWintrustProcedure(
    const HMODULE module,
    const char* name)
{
    const FARPROC procedure = GetProcAddress(module, name);
    if (procedure == nullptr)
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            std::string("GetProcAddress(") + name + ")");
    }
    return procedure;
}
void VerifyExactCatalogMemberAndTrust(
    const fs::path& catalogPath,
    const fs::path& memberPath)
{
    UniqueModule wintrust(LoadLibraryExW(
        L"wintrust.dll",
        nullptr,
        LOAD_LIBRARY_SEARCH_SYSTEM32));
    if (!wintrust)
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "LoadLibraryExW(wintrust.dll)");
    }
    const auto acquireContext =
        reinterpret_cast<AcquireCatalogContext2Function>(
            RequiredWintrustProcedure(
                wintrust.get(),
                "CryptCATAdminAcquireContext2"));
    const auto calculateHash =
        reinterpret_cast<CalculateCatalogHash2Function>(
            RequiredWintrustProcedure(
                wintrust.get(),
                "CryptCATAdminCalcHashFromFileHandle2"));

    const GUID driverAction = DRIVER_ACTION_VERIFY;
    HCATADMIN rawAdmin = nullptr;
    if (!acquireContext(
            &rawAdmin,
            &driverAction,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            0))
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "CryptCATAdminAcquireContext2");
    }
    UniqueCatalogAdmin admin(rawAdmin);

    UniqueHandle memberFile(CreateFileW(
        memberPath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr));
    if (!memberFile || memberFile.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "CreateFileW(catalog member)");
    }

    DWORD digestSize = 0;
    if (!calculateHash(
            admin.get(),
            memberFile.get(),
            &digestSize,
            nullptr,
            0))
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "CryptCATAdminCalcHashFromFileHandle2(size)");
    }
    if (digestSize != 32U)
    {
        throw InstallerError(
            ExitCode::SignatureInvalid,
            "Catalog member digest is not SHA-256 sized.");
    }

    std::vector<unsigned char> digest(digestSize);
    if (!calculateHash(
            admin.get(),
            memberFile.get(),
            &digestSize,
            digest.data(),
            0) ||
        digestSize != static_cast<DWORD>(digest.size()))
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "CryptCATAdminCalcHashFromFileHandle2");
    }

    const std::wstring memberTag = WidenAscii(Hex(digest));
    std::wstring mutableCatalogPath = catalogPath.wstring();
    HANDLE rawCatalog = CryptCATOpen(
        mutableCatalogPath.data(),
        CRYPTCAT_OPEN_EXISTING,
        0,
        CRYPTCAT_VERSION_1,
        0);
    if (rawCatalog == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "CryptCATOpen");
    }
    UniqueCatalogHandle catalog(rawCatalog);

    std::wstring mutableMemberTag = memberTag;
    const CRYPTCATMEMBER* member = CryptCATGetMemberInfo(
        catalog.get(),
        mutableMemberTag.data());
    if (member == nullptr ||
        member->pwszReferenceTag == nullptr ||
        member->pIndirectData == nullptr ||
        member->pIndirectData->Digest.pbData == nullptr ||
        !EqualsInsensitive(member->pwszReferenceTag, memberTag) ||
        member->pIndirectData->DigestAlgorithm.pszObjId == nullptr ||
        lstrcmpA(
            member->pIndirectData->DigestAlgorithm.pszObjId,
            szOID_NIST_sha256) != 0 ||
        member->pIndirectData->Digest.cbData !=
            static_cast<DWORD>(digest.size()) ||
        !std::equal(
            digest.begin(),
            digest.end(),
            member->pIndirectData->Digest.pbData))
    {
        throw InstallerError(
            ExitCode::SignatureInvalid,
            "The exact Phase 2 SYS digest is not a member of the exact catalog.");
    }

    LARGE_INTEGER fileStart{};
    if (!SetFilePointerEx(
            memberFile.get(),
            fileStart,
            nullptr,
            FILE_BEGIN))
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "SetFilePointerEx(catalog member)");
    }

    WINTRUST_CATALOG_INFO catalogInfo{};
    catalogInfo.cbStruct = sizeof(catalogInfo);
    catalogInfo.pcwszCatalogFilePath = mutableCatalogPath.c_str();
    catalogInfo.pcwszMemberTag = memberTag.c_str();
    catalogInfo.pcwszMemberFilePath = memberPath.c_str();
    catalogInfo.hMemberFile = memberFile.get();
    catalogInfo.pbCalculatedFileHash = digest.data();
    catalogInfo.cbCalculatedFileHash =
        static_cast<DWORD>(digest.size());
    catalogInfo.hCatAdmin = admin.get();

    WINTRUST_DATA trustData{};
    trustData.cbStruct = sizeof(trustData);
    trustData.dwUIChoice = WTD_UI_NONE;
    trustData.fdwRevocationChecks = WTD_REVOKE_NONE;
    trustData.dwUnionChoice = WTD_CHOICE_CATALOG;
    trustData.pCatalog = &catalogInfo;
    trustData.dwStateAction = WTD_STATEACTION_VERIFY;
    trustData.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;

    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    const LONG result = WinVerifyTrust(
        nullptr,
        &action,
        &trustData);
    trustData.dwStateAction = WTD_STATEACTION_CLOSE;
    (void)WinVerifyTrust(nullptr, &action, &trustData);
    if (result != ERROR_SUCCESS)
    {
        throw InstallerError(
            ExitCode::SignatureInvalid,
            "Catalog member trust verification failed with status " +
                std::to_string(result) + ".");
    }
}

void VerifyInfCatalogTrust(const fs::path& infPath)
{
    SP_INF_SIGNER_INFO_W signer{};
    signer.cbSize = sizeof(signer);
    if (!SetupVerifyInfFileW(infPath.c_str(), nullptr, &signer))
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "SetupVerifyInfFileW");
    }
    if (!EqualsInsensitive(
            fs::path(signer.CatalogFile).filename().wstring(),
            kCatName))
    {
        throw InstallerError(
            ExitCode::SignatureInvalid,
            "INF resolved to an unexpected catalog.");
    }
}

[[nodiscard]] PackagePaths ValidatePackage(
    const fs::path& inputDirectory,
    const Manifest& manifest)
{
    const fs::path directory = FullPath(inputDirectory);
    InspectLocalDirectory(directory);
    VerifyExactPackageFileSet(directory);
    const PackagePaths package{
        directory,
        directory / kInfName,
        directory / kCatName,
        directory / kSysName};
    const LocalFileMetadata inf =
        InspectLocalRegularFile(package.inf, directory);
    const LocalFileMetadata cat =
        InspectLocalRegularFile(package.cat, directory);
    const LocalFileMetadata sys =
        InspectLocalRegularFile(package.sys, directory);
    if (inf.size != manifest.infSize ||
        cat.size != manifest.catSize ||
        sys.size != manifest.sysSize ||
        Sha256File(package.inf) != manifest.infSha256 ||
        Sha256File(package.cat) != manifest.catSha256 ||
        Sha256File(package.sys) != manifest.sysSha256)
    {
        throw InstallerError(
            ExitCode::PackageHashMismatch,
            "Package byte sizes or SHA-256 values do not match the pinned manifest.");
    }
    VerifyInfIdentity(package.inf);
    VerifyInfCatalogTrust(package.inf);
    VerifyAuthenticodeFile(package.cat);
    VerifyExactCatalogMemberAndTrust(package.cat, package.sys);
    return package;
}
void ClearExactStagingDirectory(const fs::path& inputDirectory)
{
    const fs::path directory = FullPath(inputDirectory);
    InspectLocalDirectory(directory);
    VerifyProtectedPathDacl(directory, true);
    const std::set<std::wstring> allowed = {
        kInfName,
        kCatName,
        kSysName,
        std::wstring(kInfName) + L".incoming",
        std::wstring(kCatName) + L".incoming",
        std::wstring(kSysName) + L".incoming"};
    WIN32_FIND_DATAW data{};
    UniqueFindHandle search(FindFirstFileW(
        (directory / L"*").c_str(),
        &data));
    if (!search || search.get() == INVALID_HANDLE_VALUE)
    {
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND)
        {
            return;
        }
        SetLastError(error);
        ThrowLastError(ExitCode::IoFailure, "FindFirstFileW(staging)");
    }
    std::vector<fs::path> files;
    do
    {
        const std::wstring name = data.cFileName;
        if (name == L"." || name == L"..")
        {
            continue;
        }
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ||
            (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ||
            allowed.find(name) == allowed.end())
        {
            throw InstallerError(
                ExitCode::Conflict,
                "Protected staging contains an unexpected object.");
        }
        const fs::path file = directory / name;
        // The protected parent is the trust boundary. A crash can leave an
        // exact incoming file before its file DACL is applied; validate that
        // it is still a local, non-reparse, single-link child and delete it.
        (void)InspectLocalRegularFile(file, directory);
        files.push_back(file);
    } while (FindNextFileW(search.get(), &data));
    const DWORD finalError = GetLastError();
    if (finalError != ERROR_NO_MORE_FILES)
    {
        SetLastError(finalError);
        ThrowLastError(ExitCode::IoFailure, "FindNextFileW(staging)");
    }
    search.reset();
    for (const fs::path& file : files)
    {
        if (!DeleteFileW(file.c_str()))
        {
            ThrowLastError(ExitCode::IoFailure, "DeleteFileW(staging)");
        }
    }
}

void CopyFileToProtectedStage(
    const fs::path& source,
    const fs::path& destination,
    const fs::path& stageDirectory)
{
    (void)InspectLocalRegularFile(source, source.parent_path());
    const fs::path incoming =
        fs::path(destination.wstring() + L".incoming");
    if (fs::exists(incoming))
    {
        (void)InspectLocalRegularFile(incoming, stageDirectory);
        if (!DeleteFileW(incoming.c_str()))
        {
            ThrowLastError(
                ExitCode::IoFailure,
                "DeleteFileW(stale staging incoming)");
        }
    }
    if (!CopyFileW(source.c_str(), incoming.c_str(), TRUE))
    {
        ThrowLastError(ExitCode::IoFailure, "CopyFileW(staging incoming)");
    }
    (void)InspectLocalRegularFile(incoming, stageDirectory);
    ApplyAndVerifyProtectedPathDacl(incoming, false);
    if (!MoveFileExW(
            incoming.c_str(),
            destination.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        (void)DeleteFileW(incoming.c_str());
        ThrowLastError(ExitCode::IoFailure, "MoveFileExW(staging commit)");
    }
    (void)InspectLocalRegularFile(destination, stageDirectory);
    VerifyProtectedPathDacl(destination, false);
}
[[nodiscard]] PackagePaths StageValidatedPackage(
    const PackagePaths& source,
    const Manifest& manifest)
{
    const fs::path stageDirectory = EnsureProtectedStagingDirectory();
    if (EqualsInsensitive(
            source.directory.wstring(),
            stageDirectory.wstring()))
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Caller package directory cannot be the protected staging directory.");
    }
    ClearExactStagingDirectory(stageDirectory);
    try
    {
        CopyFileToProtectedStage(
            source.inf,
            stageDirectory / kInfName,
            stageDirectory);
        CopyFileToProtectedStage(
            source.cat,
            stageDirectory / kCatName,
            stageDirectory);
        CopyFileToProtectedStage(
            source.sys,
            stageDirectory / kSysName,
            stageDirectory);
        const PackagePaths staged =
            ValidatePackage(stageDirectory, manifest);
        VerifyProtectedPathDacl(staged.inf, false);
        VerifyProtectedPathDacl(staged.cat, false);
        VerifyProtectedPathDacl(staged.sys, false);
        return staged;
    }
    catch (...)
    {
        try
        {
            ClearExactStagingDirectory(stageDirectory);
        }
        catch (...)
        {
        }
        throw;
    }
}

[[nodiscard]] PackagePaths ValidateProtectedStagingPackage(
    const fs::path& inputDirectory,
    const Manifest& manifest)
{
    const fs::path directory = FullPath(inputDirectory);
    if (!EqualsInsensitive(
            directory.wstring(),
            FullPath(DefaultStagingDirectory()).wstring()))
    {
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Recorded staging path is not the fixed production path.");
    }
    InspectLocalDirectory(directory.parent_path());
    VerifyProtectedPathDacl(directory.parent_path(), true);
    InspectLocalDirectory(directory);
    VerifyProtectedPathDacl(directory, true);
    const PackagePaths package = ValidatePackage(directory, manifest);
    VerifyProtectedPathDacl(package.inf, false);
    VerifyProtectedPathDacl(package.cat, false);
    VerifyProtectedPathDacl(package.sys, false);
    return package;
}

[[nodiscard]] fs::path WindowsDirectory()
{
    const UINT required = GetWindowsDirectoryW(nullptr, 0);
    if (required == 0)
    {
        ThrowLastError(ExitCode::IoFailure, "GetWindowsDirectoryW");
    }
    std::vector<wchar_t> value(static_cast<size_t>(required) + 1U);
    const UINT written = GetWindowsDirectoryW(
        value.data(),
        static_cast<UINT>(value.size()));
    if (written == 0 || written >= static_cast<UINT>(value.size()))
    {
        ThrowLastError(ExitCode::IoFailure, "GetWindowsDirectoryW");
    }
    return FullPath(fs::path(value.data()));
}

[[nodiscard]] std::vector<INFCONTEXT> GetOptionalInfSectionLines(
    const HINF inf,
    const std::wstring& section)
{
    const LONG count = SetupGetLineCountW(inf, section.c_str());
    if (count <= 0)
    {
        return {};
    }
    INFCONTEXT context{};
    if (!SetupFindFirstLineW(inf, section.c_str(), nullptr, &context))
    {
        ThrowInfIdentity("Referenced INF section could not be opened.");
    }
    std::vector<INFCONTEXT> lines;
    lines.reserve(static_cast<size_t>(count));
    lines.push_back(context);
    for (LONG index = 1; index < count; ++index)
    {
        INFCONTEXT next{};
        if (!SetupFindNextLine(&context, &next))
        {
            ThrowInfIdentity("Referenced INF section ended unexpectedly.");
        }
        lines.push_back(next);
        context = next;
    }
    INFCONTEXT extra{};
    if (SetupFindNextLine(&context, &extra))
    {
        ThrowInfIdentity("Referenced INF section count changed during inspection.");
    }
    return lines;
}

[[nodiscard]] std::optional<std::wstring> TryGetActualModelsSection(
    const INFCONTEXT& inputContext)
{
    INFCONTEXT context = inputContext;
    DWORD required = 0;
    if (!SetupDiGetActualModelsSectionW(
            &context,
            nullptr,
            nullptr,
            0,
            &required,
            nullptr) ||
        required == 0)
    {
        return std::nullopt;
    }
    std::vector<wchar_t> section(required);
    if (!SetupDiGetActualModelsSectionW(
            &context,
            nullptr,
            section.data(),
            static_cast<DWORD>(section.size()),
            nullptr,
            nullptr))
    {
        return std::nullopt;
    }
    return std::wstring(section.data());
}

[[nodiscard]] std::optional<std::wstring> TryGetActualInstallSection(
    const HINF inf,
    const std::wstring& baseSection)
{
    DWORD required = 0;
    if (!SetupDiGetActualSectionToInstallW(
            inf,
            baseSection.c_str(),
            nullptr,
            0,
            &required,
            nullptr) ||
        required == 0)
    {
        return std::nullopt;
    }
    std::vector<wchar_t> section(required);
    if (!SetupDiGetActualSectionToInstallW(
            inf,
            baseSection.c_str(),
            section.data(),
            static_cast<DWORD>(section.size()),
            nullptr,
            nullptr))
    {
        return std::nullopt;
    }
    return std::wstring(section.data());
}

[[nodiscard]] bool IsPhase2InfCandidate(const fs::path& path)
{
    UINT errorLine = 0;
    UniqueInfHandle inf(SetupOpenInfFileW(
        path.c_str(),
        nullptr,
        INF_STYLE_WIN4,
        &errorLine));
    if (!inf || inf.get() == INVALID_HANDLE_VALUE)
    {
        // A published OEM INF that SetupAPI cannot parse is an inventory
        // ambiguity; never declare a clean machine around it.
        return true;
    }

    try
    {
        const std::vector<INFCONTEXT> manufacturerLines =
            GetOptionalInfSectionLines(inf.get(), L"Manufacturer");
        for (const INFCONTEXT& manufacturer : manufacturerLines)
        {
            const std::optional<std::wstring> modelSection =
                TryGetActualModelsSection(manufacturer);
            if (!modelSection.has_value())
            {
                // A decoration that is not applicable to this machine cannot
                // compete for the exact x64 root device.
                continue;
            }
            const std::vector<INFCONTEXT> models =
                GetOptionalInfSectionLines(inf.get(), modelSection.value());
            if (models.empty())
            {
                return true;
            }
            for (const INFCONTEXT& model : models)
            {
                INFCONTEXT mutableModel = model;
                const DWORD fieldCount =
                    SetupGetFieldCount(&mutableModel);
                if (fieldCount < 2)
                {
                    return true;
                }
                const std::optional<std::wstring> installBase =
                    TryGetInfStringField(model, 1);
                if (!installBase.has_value() || installBase->empty())
                {
                    return true;
                }
                bool exactHardwareId = false;
                for (DWORD field = 2; field <= fieldCount; ++field)
                {
                    const std::optional<std::wstring> id =
                        TryGetInfStringField(model, field);
                    if (!id.has_value())
                    {
                        return true;
                    }
                    if (EqualsInsensitive(id.value(), kHardwareId))
                    {
                        exactHardwareId = true;
                    }
                }

                const std::optional<std::wstring> installSection =
                    TryGetActualInstallSection(
                        inf.get(),
                        installBase.value());
                if (!installSection.has_value())
                {
                    return true;
                }
                const std::vector<INFCONTEXT> services =
                    GetOptionalInfSectionLines(
                        inf.get(),
                        installSection.value() + L".Services");
                for (const INFCONTEXT& service : services)
                {
                    const std::optional<std::wstring> key =
                        TryGetInfStringField(service, 0);
                    if (!key.has_value() ||
                        !EqualsInsensitive(key.value(), L"AddService"))
                    {
                        continue;
                    }
                    INFCONTEXT mutableService = service;
                    if (SetupGetFieldCount(&mutableService) < 1)
                    {
                        return true;
                    }
                    const std::optional<std::wstring> serviceName =
                        TryGetInfStringField(service, 1);
                    if (!serviceName.has_value())
                    {
                        return true;
                    }
                    if (EqualsInsensitive(
                            serviceName.value(),
                            kServiceName))
                    {
                        return true;
                    }
                }
                if (exactHardwareId)
                {
                    return true;
                }
            }
        }
        return false;
    }
    catch (const InstallerError&)
    {
        // Fail closed when a syntactically published OEM INF has ambiguous
        // referenced model or service data.
        return true;
    }
}
[[nodiscard]] std::vector<fs::path> FindPhase2PublishedInfs()
{
    const fs::path infDirectory = WindowsDirectory() / L"INF";
    const fs::path pattern = infDirectory / L"oem*.inf";
    WIN32_FIND_DATAW data{};
    UniqueFindHandle search(FindFirstFileW(pattern.c_str(), &data));
    if (!search || search.get() == INVALID_HANDLE_VALUE)
    {
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND)
        {
            return {};
        }
        SetLastError(error);
        ThrowLastError(ExitCode::IoFailure, "FindFirstFileW");
    }

    std::vector<fs::path> matches;
    do
    {
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0 &&
            (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
        {
            const fs::path candidate = infDirectory / data.cFileName;
            if (IsPhase2InfCandidate(candidate))
            {
                matches.push_back(candidate);
            }
        }
    } while (FindNextFileW(search.get(), &data));
    const DWORD finalError = GetLastError();
    if (finalError != ERROR_NO_MORE_FILES)
    {
        SetLastError(finalError);
        ThrowLastError(ExitCode::IoFailure, "FindNextFileW");
    }
    return matches;
}

[[nodiscard]] std::optional<fs::path> FindMatchingPublishedInf(
    const std::vector<fs::path>& candidates,
    const Manifest& manifest)
{
    std::optional<fs::path> result;
    for (const fs::path& candidate : candidates)
    {
        if (Sha256File(candidate) == manifest.infSha256)
        {
            if (result.has_value())
            {
                throw InstallerError(
                    ExitCode::Conflict,
                    "More than one matching published INF exists.");
            }
            result = candidate;
        }
    }
    return result;
}
[[nodiscard]] std::wstring GetDeviceInstanceId(
    const HDEVINFO set,
    SP_DEVINFO_DATA& data)
{
    DWORD required = 0;
    (void)SetupDiGetDeviceInstanceIdW(
        set,
        &data,
        nullptr,
        0,
        &required);
    if (required == 0)
    {
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupDiGetDeviceInstanceIdW");
    }
    std::vector<wchar_t> value(required);
    if (!SetupDiGetDeviceInstanceIdW(
            set,
            &data,
            value.data(),
            static_cast<DWORD>(value.size()),
            nullptr))
    {
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupDiGetDeviceInstanceIdW");
    }
    return value.data();
}

struct RegistryPropertyBytes
{
    DWORD type{};
    std::vector<unsigned char> bytes;
};

[[nodiscard]] RegistryPropertyBytes GetRegistryPropertyBytes(
    const HDEVINFO set,
    SP_DEVINFO_DATA& data,
    const DWORD property)
{
    DWORD type = 0;
    DWORD required = 0;
    (void)SetupDiGetDeviceRegistryPropertyW(
        set,
        &data,
        property,
        &type,
        nullptr,
        0,
        &required);
    if (required == 0)
    {
        const DWORD error = GetLastError();
        if (error == ERROR_INVALID_DATA || error == ERROR_NOT_FOUND)
        {
            return {};
        }
        SetLastError(error);
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupDiGetDeviceRegistryPropertyW");
    }
    std::vector<unsigned char> bytes(required);
    DWORD returnedType = 0;
    DWORD returnedSize = 0;
    if (!SetupDiGetDeviceRegistryPropertyW(
            set,
            &data,
            property,
            &returnedType,
            bytes.data(),
            static_cast<DWORD>(bytes.size()),
            &returnedSize) ||
        returnedType != type || returnedSize != required)
    {
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupDiGetDeviceRegistryPropertyW");
    }
    return RegistryPropertyBytes{returnedType, std::move(bytes)};
}

[[nodiscard]] std::wstring GetRegistryStringProperty(
    const HDEVINFO set,
    SP_DEVINFO_DATA& data,
    const DWORD property)
{
    const RegistryPropertyBytes value =
        GetRegistryPropertyBytes(set, data, property);
    if (value.bytes.empty())
    {
        return {};
    }
    if (value.type != REG_SZ ||
        value.bytes.size() < sizeof(wchar_t) ||
        value.bytes.size() % sizeof(wchar_t) != 0)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Device registry string has an invalid type or size.");
    }
    const auto* characters =
        reinterpret_cast<const wchar_t*>(value.bytes.data());
    const size_t count = value.bytes.size() / sizeof(wchar_t);
    if (characters[count - 1U] != L'\0' ||
        wcsnlen_s(characters, count) != count - 1U)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Device registry string is not exactly NUL terminated.");
    }
    return std::wstring(characters, count - 1U);
}

[[nodiscard]] std::vector<std::wstring> GetHardwareIds(
    const HDEVINFO set,
    SP_DEVINFO_DATA& data)
{
    const RegistryPropertyBytes value =
        GetRegistryPropertyBytes(set, data, SPDRP_HARDWAREID);
    std::vector<std::wstring> values;
    if (value.bytes.empty())
    {
        return values;
    }
    if (value.type != REG_MULTI_SZ ||
        value.bytes.size() < 2U * sizeof(wchar_t) ||
        value.bytes.size() % sizeof(wchar_t) != 0)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Hardware IDs have an invalid type or size.");
    }
    const auto* characters =
        reinterpret_cast<const wchar_t*>(value.bytes.data());
    const size_t count = value.bytes.size() / sizeof(wchar_t);
    if (characters[count - 1U] != L'\0' ||
        characters[count - 2U] != L'\0')
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Hardware IDs are not double-NUL terminated.");
    }
    size_t offset = 0;
    while (offset + 1U < count && characters[offset] != L'\0')
    {
        const size_t remaining = count - offset;
        const size_t length = wcsnlen_s(characters + offset, remaining);
        if (length == 0 || length >= remaining)
        {
            throw InstallerError(
                ExitCode::VerificationFailed,
                "Hardware IDs contain malformed MULTI_SZ data.");
        }
        values.emplace_back(characters + offset, length);
        offset += length + 1U;
    }
    if (offset != count - 1U || values.empty())
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Hardware IDs contain trailing or empty MULTI_SZ data.");
    }
    return values;
}
[[nodiscard]] std::wstring GetDevicePropertyString(
    const HDEVINFO set,
    SP_DEVINFO_DATA& data,
    const DEVPROPKEY& key)
{
    DEVPROPTYPE type = 0;
    DWORD required = 0;
    (void)SetupDiGetDevicePropertyW(
        set,
        &data,
        &key,
        &type,
        nullptr,
        0,
        &required,
        0);
    if (required == 0)
    {
        const DWORD error = GetLastError();
        if (error == ERROR_NOT_FOUND || error == ERROR_INVALID_DATA)
        {
            return {};
        }
        SetLastError(error);
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupDiGetDevicePropertyW");
    }
    std::vector<unsigned char> bytes(required);
    DWORD returnedSize = 0;
    DEVPROPTYPE returnedType = 0;
    if (!SetupDiGetDevicePropertyW(
            set,
            &data,
            &key,
            &returnedType,
            bytes.data(),
            static_cast<DWORD>(bytes.size()),
            &returnedSize,
            0) ||
        returnedType != DEVPROP_TYPE_STRING ||
        returnedType != type || returnedSize != required ||
        bytes.size() < sizeof(wchar_t) ||
        bytes.size() % sizeof(wchar_t) != 0)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Device property string has an invalid type or size.");
    }
    const auto* characters =
        reinterpret_cast<const wchar_t*>(bytes.data());
    const size_t count = bytes.size() / sizeof(wchar_t);
    if (characters[count - 1U] != L'\0' ||
        wcsnlen_s(characters, count) != count - 1U)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Device property string is not exactly NUL terminated.");
    }
    return std::wstring(characters, count - 1U);
}
[[nodiscard]] std::vector<DeviceRecord> EnumeratePhase2Roots(
    const bool presentOnly)
{
    DWORD flags = DIGCF_ALLCLASSES;
    if (presentOnly)
    {
        flags |= DIGCF_PRESENT;
    }
    UniqueDeviceInfoSet set(SetupDiGetClassDevsW(
        nullptr,
        nullptr,
        nullptr,
        flags));
    if (!set || set.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupDiGetClassDevsW");
    }

    std::vector<DeviceRecord> records;
    for (DWORD index = 0;; ++index)
    {
        SP_DEVINFO_DATA data{};
        data.cbSize = sizeof(data);
        if (!SetupDiEnumDeviceInfo(set.get(), index, &data))
        {
            const DWORD error = GetLastError();
            if (error == ERROR_NO_MORE_ITEMS)
            {
                break;
            }
            SetLastError(error);
            ThrowLastError(
                ExitCode::VerificationFailed,
                "SetupDiEnumDeviceInfo");
        }
        const std::wstring instanceId =
            GetDeviceInstanceId(set.get(), data);
        const std::vector<std::wstring> ids =
            GetHardwareIds(set.get(), data);
        const bool hardwareIdMatches = std::any_of(
            ids.begin(),
            ids.end(),
            [](const std::wstring& id) {
                return EqualsInsensitive(id, kHardwareId);
            });
        const bool instanceIdMatches =
            EqualsInsensitive(instanceId, kRootInstanceId);
        if (!hardwareIdMatches && !instanceIdMatches)
        {
            continue;
        }

        DeviceRecord record{};
        record.instanceId = instanceId;
        record.hardwareIds = ids;
        record.serviceName = GetRegistryStringProperty(
            set.get(),
            data,
            SPDRP_SERVICE);
        record.publishedInf = GetDevicePropertyString(
            set.get(),
            data,
            DEVPKEY_Device_DriverInfPath);
        record.enumeratorName = GetRegistryStringProperty(
            set.get(),
            data,
            SPDRP_ENUMERATOR_NAME);
        record.classGuid = data.ClassGuid;
        record.devInst = data.DevInst;
        const CONFIGRET statusResult = CM_Get_DevNode_Status(
            &record.status,
            &record.problem,
            data.DevInst,
            0);
        if (statusResult != CR_SUCCESS)
        {
            if (!presentOnly && statusResult == CR_NO_SUCH_DEVNODE)
            {
                record.status = 0;
                record.problem = CM_PROB_PHANTOM;
            }
            else
            {
                throw InstallerError(
                    ExitCode::VerificationFailed,
                    "CM_Get_DevNode_Status failed.");
            }
        }
        records.push_back(std::move(record));
    }
    return records;
}

[[nodiscard]] std::vector<std::wstring> EnumerateControlInterfaces()
{
    UniqueDeviceInfoSet set(SetupDiGetClassDevsW(
        &kControlInterfaceGuid,
        nullptr,
        nullptr,
        DIGCF_DEVICEINTERFACE | DIGCF_PRESENT));
    if (!set || set.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupDiGetClassDevsW(interface)");
    }

    std::vector<std::wstring> instances;
    for (DWORD index = 0;; ++index)
    {
        SP_DEVICE_INTERFACE_DATA interfaceData{};
        interfaceData.cbSize = sizeof(interfaceData);
        if (!SetupDiEnumDeviceInterfaces(
                set.get(),
                nullptr,
                &kControlInterfaceGuid,
                index,
                &interfaceData))
        {
            const DWORD error = GetLastError();
            if (error == ERROR_NO_MORE_ITEMS)
            {
                break;
            }
            SetLastError(error);
            ThrowLastError(
                ExitCode::VerificationFailed,
                "SetupDiEnumDeviceInterfaces");
        }

        DWORD required = 0;
        SP_DEVINFO_DATA data{};
        data.cbSize = sizeof(data);
        (void)SetupDiGetDeviceInterfaceDetailW(
            set.get(),
            &interfaceData,
            nullptr,
            0,
            &required,
            &data);
        if (required == 0 || GetLastError() != ERROR_INSUFFICIENT_BUFFER)
        {
            ThrowLastError(
                ExitCode::VerificationFailed,
                "SetupDiGetDeviceInterfaceDetailW(size)");
        }
        std::vector<unsigned char> detailStorage(required);
        auto* detail = reinterpret_cast<PSP_DEVICE_INTERFACE_DETAIL_DATA_W>(
            detailStorage.data());
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
        if (!SetupDiGetDeviceInterfaceDetailW(
                set.get(),
                &interfaceData,
                detail,
                required,
                nullptr,
                &data))
        {
            ThrowLastError(
                ExitCode::VerificationFailed,
                "SetupDiGetDeviceInterfaceDetailW");
        }
        instances.push_back(GetDeviceInstanceId(set.get(), data));
    }
    return instances;
}

[[nodiscard]] ServiceRecord QueryServiceRecordFromHandle(
    const SC_HANDLE service,
    const ExitCode code)
{
    SERVICE_STATUS_PROCESS status{};
    DWORD returned = 0;
    if (!QueryServiceStatusEx(
            service,
            SC_STATUS_PROCESS_INFO,
            reinterpret_cast<LPBYTE>(&status),
            sizeof(status),
            &returned))
    {
        ThrowLastError(code, "QueryServiceStatusEx");
    }
    DWORD required = 0;
    (void)QueryServiceConfigW(service, nullptr, 0, &required);
    if (required < sizeof(QUERY_SERVICE_CONFIGW) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER)
    {
        ThrowLastError(code, "QueryServiceConfigW(size)");
    }
    std::vector<unsigned char> buffer(required);
    auto* config = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(buffer.data());
    DWORD returnedSize = 0;
    if (!QueryServiceConfigW(
            service,
            config,
            required,
            &returnedSize))
    {
        ThrowLastError(code, "QueryServiceConfigW");
    }
    if (config->lpBinaryPathName == nullptr ||
        config->lpBinaryPathName[0] == L'\0')
    {
        throw InstallerError(
            code,
            "Kernel service has an empty binary path.");
    }
    return ServiceRecord{
        true,
        status.dwCurrentState,
        config->dwServiceType,
        config->dwStartType,
        std::wstring(config->lpBinaryPathName)};
}

[[nodiscard]] ServiceRecord QueryDriverService()
{
    UniqueServiceHandle manager(OpenSCManagerW(
        nullptr,
        nullptr,
        SC_MANAGER_CONNECT));
    if (!manager)
    {
        ThrowLastError(
            ExitCode::VerificationFailed,
            "OpenSCManagerW");
    }
    UniqueServiceHandle service;
    for (unsigned int attempt = 0; attempt < 40U; ++attempt)
    {
        service.reset(OpenServiceW(
            manager.get(),
            kServiceName,
            SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG));
        if (service)
        {
            break;
        }
        const DWORD error = GetLastError();
        if (error == ERROR_SERVICE_DOES_NOT_EXIST)
        {
            return {};
        }
        if (error != ERROR_SERVICE_MARKED_FOR_DELETE)
        {
            SetLastError(error);
            ThrowLastError(
                ExitCode::VerificationFailed,
                "OpenServiceW");
        }
        Sleep(250);
    }
    if (!service)
    {
        throw InstallerError(
            ExitCode::ServiceCleanupFailed,
            "Driver service remained marked for deletion.");
    }
    return QueryServiceRecordFromHandle(
        service.get(),
        ExitCode::VerificationFailed);
}
[[nodiscard]] Inventory CollectInventory(const Manifest& manifest)
{
    Inventory inventory{};
    inventory.roots = EnumeratePhase2Roots(false);
    inventory.candidatePublishedInfs = FindPhase2PublishedInfs();
    inventory.matchingPublishedInf = FindMatchingPublishedInf(
        inventory.candidatePublishedInfs,
        manifest);
    inventory.service = QueryDriverService();
    inventory.interfaceInstances = EnumerateControlInterfaces();
    return inventory;
}

[[nodiscard]] bool IsInventoryClean(const Inventory& inventory)
{
    return inventory.roots.empty() &&
        inventory.candidatePublishedInfs.empty() &&
        !inventory.matchingPublishedInf.has_value() &&
        !inventory.service.exists &&
        inventory.interfaceInstances.empty();
}

[[nodiscard]] bool IsPublishedInfName(const std::wstring& value)
{
    if (!StartsWithInsensitive(value, L"oem") ||
        !EndsWithInsensitive(value, L".inf") ||
        value.size() <= 7)
    {
        return false;
    }
    return std::all_of(
        value.begin() + 3,
        value.end() - 4,
        [](const wchar_t ch) { return std::iswdigit(ch) != 0; });
}

[[nodiscard]] fs::path GetDriverStoreInf(const fs::path& publishedInf)
{
    DWORD required = 0;
    (void)SetupGetInfDriverStoreLocationW(
        publishedInf.c_str(),
        nullptr,
        nullptr,
        nullptr,
        0,
        &required);
    if (required == 0 || GetLastError() != ERROR_INSUFFICIENT_BUFFER)
    {
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupGetInfDriverStoreLocationW(size)");
    }
    std::vector<wchar_t> location(required);
    if (!SetupGetInfDriverStoreLocationW(
            publishedInf.c_str(),
            nullptr,
            nullptr,
            location.data(),
            static_cast<DWORD>(location.size()),
            nullptr))
    {
        ThrowLastError(
            ExitCode::VerificationFailed,
            "SetupGetInfDriverStoreLocationW");
    }
    return FullPath(fs::path(location.data()));
}

[[nodiscard]] fs::path ResolveCatalogForInf(const fs::path& infPath)
{
    SP_INF_SIGNER_INFO_W signer{};
    signer.cbSize = sizeof(signer);
    if (!SetupVerifyInfFileW(infPath.c_str(), nullptr, &signer))
    {
        ThrowLastError(
            ExitCode::SignatureInvalid,
            "SetupVerifyInfFileW(installed)");
    }
    fs::path catalog(signer.CatalogFile);
    if (!catalog.is_absolute())
    {
        catalog = infPath.parent_path() / catalog;
    }
    catalog = FullPath(catalog);
    RequireRegularNonReparseFile(
        catalog,
        ExitCode::SignatureInvalid,
        "Installed catalog");
    return catalog;
}

struct InstalledPackage
{
    fs::path publishedInf;
    fs::path storeInf;
    fs::path catalog;
    fs::path sys;
};

[[nodiscard]] InstalledPackage VerifyInstalledPackage(
    const fs::path& publishedInf,
    const Manifest& manifest)
{
    const fs::path exactPublished = FullPath(publishedInf);
    if (!IsPublishedInfName(exactPublished.filename().wstring()))
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Published INF name is invalid.");
    }
    RequireRegularNonReparseFile(
        exactPublished,
        ExitCode::VerificationFailed,
        "Published INF");
    if (Sha256File(exactPublished) != manifest.infSha256)
    {
        throw InstallerError(
            ExitCode::PackageHashMismatch,
            "Published INF hash does not match the manifest.");
    }
    VerifyInfIdentity(exactPublished);

    const fs::path storeInf = GetDriverStoreInf(exactPublished);
    const fs::path storeDirectory = storeInf.parent_path();
    RequireOrdinaryDirectory(storeDirectory, "Driver Store directory");
    const fs::path sys = storeDirectory / kSysName;
    const fs::path catalog = ResolveCatalogForInf(storeInf);
    const LocalFileMetadata storeInfMetadata =
        InspectLocalRegularFile(storeInf, storeDirectory);
    const LocalFileMetadata sysMetadata =
        InspectLocalRegularFile(sys, storeDirectory);
    const LocalFileMetadata catalogMetadata =
        InspectLocalRegularFile(catalog, storeDirectory);
    if (storeInfMetadata.size != manifest.infSize ||
        sysMetadata.size != manifest.sysSize ||
        catalogMetadata.size != manifest.catSize ||
        Sha256File(storeInf) != manifest.infSha256 ||
        Sha256File(sys) != manifest.sysSha256 ||
        Sha256File(catalog) != manifest.catSha256)
    {
        throw InstallerError(
            ExitCode::PackageHashMismatch,
            "Installed Driver Store package hashes do not match the manifest.");
    }
    VerifyInfIdentity(storeInf);
    VerifyAuthenticodeFile(catalog);
    VerifyExactCatalogMemberAndTrust(catalog, sys);
    return InstalledPackage{exactPublished, storeInf, catalog, FullPath(sys)};
}

[[nodiscard]] std::wstring NormalizeServiceBinaryPath(
    std::wstring value)
{
    if (value.size() >= 2 && value.front() == L'"' && value.back() == L'"')
    {
        value = value.substr(1, value.size() - 2);
    }
    constexpr wchar_t systemRootPrefix[] = L"\\SystemRoot\\";
    if (StartsWithInsensitive(value, systemRootPrefix))
    {
        value = (
            WindowsDirectory() /
            value.substr(std::size(systemRootPrefix) - 1U)).wstring();
    }
    else if (value.find(L'%') != std::wstring::npos)
    {
        const DWORD required = ExpandEnvironmentStringsW(
            value.c_str(),
            nullptr,
            0);
        if (required == 0)
        {
            ThrowLastError(
                ExitCode::VerificationFailed,
                "ExpandEnvironmentStringsW");
        }
        std::vector<wchar_t> expanded(required);
        if (ExpandEnvironmentStringsW(
                value.c_str(),
                expanded.data(),
                static_cast<DWORD>(expanded.size())) == 0)
        {
            ThrowLastError(
                ExitCode::VerificationFailed,
                "ExpandEnvironmentStringsW");
        }
        value = expanded.data();
    }
    return FullPath(fs::path(value)).wstring();
}

struct InputChildren
{
    std::vector<std::wstring> keyboards;
    std::vector<std::wstring> mice;
    std::vector<std::wstring> all;
};

[[nodiscard]] std::wstring GetDevNodeInstanceId(const DEVINST devInst)
{
    wchar_t value[MAX_DEVICE_ID_LEN]{};
    const CONFIGRET result = CM_Get_Device_IDW(
        devInst,
        value,
        MAX_DEVICE_ID_LEN,
        0);
    if (result != CR_SUCCESS)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "CM_Get_Device_IDW failed.");
    }
    return value;
}

[[nodiscard]] std::optional<GUID> GetDevNodeClassGuid(
    const DEVINST devInst)
{
    GUID value{};
    DEVPROPTYPE type = 0;
    ULONG size = sizeof(value);
    const CONFIGRET result = CM_Get_DevNode_PropertyW(
        devInst,
        &DEVPKEY_Device_ClassGuid,
        &type,
        reinterpret_cast<PBYTE>(&value),
        &size,
        0);
    if (result == CR_NO_SUCH_VALUE)
    {
        return std::nullopt;
    }
    if (result != CR_SUCCESS ||
        type != DEVPROP_TYPE_GUID ||
        size != static_cast<ULONG>(sizeof(value)))
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "CM_Get_DevNode_PropertyW(ClassGuid) failed.");
    }
    return value;
}

void CollectChildrenRecursive(
    const DEVINST parent,
    const unsigned int depth,
    std::set<DEVINST>& visited,
    InputChildren& children)
{
    if (depth >= 16U)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "PnP descendant tree exceeded the safety depth.");
    }
    DEVINST child = 0;
    CONFIGRET result = CM_Get_Child(&child, parent, 0);
    if (result == CR_NO_SUCH_DEVNODE)
    {
        return;
    }
    if (result != CR_SUCCESS)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "CM_Get_Child failed.");
    }

    for (;;)
    {
        if (!visited.insert(child).second)
        {
            throw InstallerError(
                ExitCode::VerificationFailed,
                "PnP descendant tree contains a cycle.");
        }
        const std::wstring instanceId = GetDevNodeInstanceId(child);
        children.all.push_back(instanceId);

        DWORD status = 0;
        ULONG problem = 0;
        if (CM_Get_DevNode_Status(&status, &problem, child, 0) != CR_SUCCESS ||
            (status & DN_STARTED) == 0 ||
            problem != 0)
        {
            throw InstallerError(
                ExitCode::VerificationFailed,
                "A Phase 2 descendant is not started cleanly.");
        }
        const std::optional<GUID> classGuid = GetDevNodeClassGuid(child);
        if (classGuid.has_value())
        {
            if (IsEqualGUID(classGuid.value(), GUID_DEVCLASS_KEYBOARD))
            {
                children.keyboards.push_back(instanceId);
            }
            else if (IsEqualGUID(classGuid.value(), GUID_DEVCLASS_MOUSE))
            {
                children.mice.push_back(instanceId);
            }
        }
        CollectChildrenRecursive(child, depth + 1U, visited, children);

        DEVINST sibling = 0;
        result = CM_Get_Sibling(&sibling, child, 0);
        if (result == CR_NO_SUCH_DEVNODE)
        {
            break;
        }
        if (result != CR_SUCCESS)
        {
            throw InstallerError(
                ExitCode::VerificationFailed,
                "CM_Get_Sibling failed.");
        }
        child = sibling;
    }
}

[[nodiscard]] InputChildren GetInputChildren(const DEVINST root)
{
    InputChildren children{};
    std::set<DEVINST> visited;
    CollectChildrenRecursive(root, 0, visited, children);
    return children;
}

[[nodiscard]] InstalledPackage VerifyInstalledState(
    const Inventory& inventory,
    const Manifest& manifest,
    const std::optional<InstallerState>& state)
{
    if (inventory.roots.size() != 1 ||
        inventory.candidatePublishedInfs.size() != 1 ||
        !inventory.matchingPublishedInf.has_value() ||
        inventory.interfaceInstances.size() != 1 ||
        !inventory.service.exists)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Installed Phase 2 inventory is incomplete or ambiguous.");
    }
    const DeviceRecord& root = inventory.roots.front();
    if (!EqualsInsensitive(root.instanceId, kRootInstanceId) ||
        root.hardwareIds.size() != 1 ||
        !EqualsInsensitive(root.hardwareIds.front(), kHardwareId) ||
        !EqualsInsensitive(root.serviceName, kServiceName) ||
        !EqualsInsensitive(root.enumeratorName, L"ROOT") ||
        IsEqualGUID(root.classGuid, GUID_DEVCLASS_SYSTEM) == FALSE ||
        !IsPublishedInfName(root.publishedInf) ||
        root.problem != 0 ||
        (root.status & DN_STARTED) == 0 ||
        !EqualsInsensitive(
            inventory.interfaceInstances.front(),
            kRootInstanceId))
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Phase 2 root device or control interface is not healthy.");
    }
    const fs::path matchingInf =
        inventory.matchingPublishedInf.value();
    if (!EqualsInsensitive(
            matchingInf.filename().wstring(),
            root.publishedInf))
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Root driver INF does not match the unique package.");
    }
    if (state.has_value())
    {
        if (state->status != "Installed" ||
            state->operation != "Install" || state->needsReboot ||
            state->manifestSha256 != manifest.manifestSha256 ||
            state->infSha256 != manifest.infSha256 ||
            state->catSha256 != manifest.catSha256 ||
            state->sysSha256 != manifest.sysSha256 ||
            !EqualsInsensitive(state->publishedInf, root.publishedInf))
        {
            throw InstallerError(
                ExitCode::RecoveryRequired,
                "Installer state does not match the installed package.");
        }
    }

    const InstalledPackage package = VerifyInstalledPackage(
        matchingInf,
        manifest);
    if (inventory.service.state != SERVICE_RUNNING ||
        inventory.service.serviceType != SERVICE_KERNEL_DRIVER ||
        inventory.service.startType != SERVICE_DEMAND_START ||
        !EqualsInsensitive(
            NormalizeServiceBinaryPath(inventory.service.binaryPath),
            package.sys.wstring()))
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Phase 2 kernel service identity or state is invalid.");
    }

    std::wstring mutableRootId = root.instanceId;
    DEVINST locatedRoot = 0;
    if (CM_Locate_DevNodeW(
            &locatedRoot,
            mutableRootId.data(),
            CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "CM_Locate_DevNodeW failed for the exact root.");
    }
    const InputChildren children = GetInputChildren(locatedRoot);
    if (children.all.size() != 3 ||
        children.keyboards.size() != 1 || children.mice.size() != 2)
    {
        throw InstallerError(
            ExitCode::VerificationFailed,
            "Expected one VHF keyboard and two VHF mice.");
    }
    return package;
}
[[nodiscard]] bool IsAdministrator()
{
    SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY;
    PSID administrators = nullptr;
    if (!AllocateAndInitializeSid(
            &authority,
            2,
            SECURITY_BUILTIN_DOMAIN_RID,
            DOMAIN_ALIAS_RID_ADMINS,
            0,
            0,
            0,
            0,
            0,
            0,
            &administrators))
    {
        ThrowLastError(
            ExitCode::InternalError,
            "AllocateAndInitializeSid");
    }
    BOOL member = FALSE;
    const BOOL checked = CheckTokenMembership(
        nullptr,
        administrators,
        &member);
    FreeSid(administrators);
    if (!checked)
    {
        ThrowLastError(
            ExitCode::InternalError,
            "CheckTokenMembership");
    }
    return member != FALSE;
}

void VerifySupportedPlatform()
{
    SYSTEM_INFO systemInfo{};
    GetNativeSystemInfo(&systemInfo);
    if (systemInfo.wProcessorArchitecture !=
        PROCESSOR_ARCHITECTURE_AMD64)
    {
        throw InstallerError(
            ExitCode::UnsupportedPlatform,
            "Only native Windows x64 is supported.");
    }

    using RtlGetVersionFunction =
        LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    const HMODULE module = GetModuleHandleW(L"ntdll.dll");
    #pragma warning(suppress : 4191)
    const auto getVersion = reinterpret_cast<RtlGetVersionFunction>(
        module == nullptr
            ? nullptr
            : GetProcAddress(module, "RtlGetVersion"));
    if (getVersion == nullptr)
    {
        throw InstallerError(
            ExitCode::UnsupportedPlatform,
            "RtlGetVersion is unavailable.");
    }
    RTL_OSVERSIONINFOW version{};
    version.dwOSVersionInfoSize = sizeof(version);
    if (getVersion(&version) != 0 ||
        version.dwMajorVersion != 10 ||
        version.dwBuildNumber != 19045)
    {
        throw InstallerError(
            ExitCode::UnsupportedPlatform,
            "This installer is restricted to Windows 10 build 19045 x64.");
    }
}

void VerifyInstallerMutexSecurity(const HANDLE mutex)
{
    PSID owner = nullptr;
    PACL dacl = nullptr;
    PSECURITY_DESCRIPTOR rawDescriptor = nullptr;
    const DWORD result = GetSecurityInfo(
        mutex,
        SE_KERNEL_OBJECT,
        OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
        &owner,
        nullptr,
        &dacl,
        nullptr,
        &rawDescriptor);
    if (result != ERROR_SUCCESS || rawDescriptor == nullptr ||
        owner == nullptr || dacl == nullptr)
    {
        SetLastError(result);
        ThrowLastError(ExitCode::IoFailure, "GetSecurityInfo(mutex)");
    }
    UniqueLocalMemory descriptor(static_cast<HLOCAL>(rawDescriptor));
    SECURITY_DESCRIPTOR_CONTROL control = 0;
    DWORD revision = 0;
    if (!GetSecurityDescriptorControl(
            rawDescriptor,
            &control,
            &revision) ||
        (control & SE_DACL_PROTECTED) == 0 ||
        dacl->AceCount != 2)
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Installer mutex security descriptor is not exact.");
    }

    std::array<unsigned char, SECURITY_MAX_SID_SIZE> systemStorage{};
    std::array<unsigned char, SECURITY_MAX_SID_SIZE> adminStorage{};
    DWORD systemSize = static_cast<DWORD>(systemStorage.size());
    DWORD adminSize = static_cast<DWORD>(adminStorage.size());
    if (!CreateWellKnownSid(
            WinLocalSystemSid,
            nullptr,
            systemStorage.data(),
            &systemSize) ||
        !CreateWellKnownSid(
            WinBuiltinAdministratorsSid,
            nullptr,
            adminStorage.data(),
            &adminSize))
    {
        ThrowLastError(ExitCode::InternalError, "CreateWellKnownSid(mutex)");
    }
    if (EqualSid(owner, adminStorage.data()) == FALSE)
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Installer mutex owner is not built-in Administrators.");
    }
    bool sawSystem = false;
    bool sawAdministrators = false;
    for (DWORD index = 0; index < dacl->AceCount; ++index)
    {
        void* rawAce = nullptr;
        if (!GetAce(dacl, index, &rawAce) || rawAce == nullptr)
        {
            ThrowLastError(ExitCode::IoFailure, "GetAce(mutex)");
        }
        const auto& ace =
            *static_cast<const ACCESS_ALLOWED_ACE*>(rawAce);
        if (ace.Header.AceType != ACCESS_ALLOWED_ACE_TYPE ||
            ace.Header.AceFlags != 0 ||
            ace.Mask != MUTEX_ALL_ACCESS)
        {
            throw InstallerError(
                ExitCode::Conflict,
                "Installer mutex contains an unexpected access rule.");
        }
        PSID sid = const_cast<DWORD*>(&ace.SidStart);
        if (EqualSid(sid, systemStorage.data()) != FALSE && !sawSystem)
        {
            sawSystem = true;
        }
        else if (EqualSid(sid, adminStorage.data()) != FALSE &&
                 !sawAdministrators)
        {
            sawAdministrators = true;
        }
        else
        {
            throw InstallerError(
                ExitCode::Conflict,
                "Installer mutex contains an unexpected principal.");
        }
    }
    if (!sawSystem || !sawAdministrators)
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Installer mutex principals are incomplete.");
    }
}

[[nodiscard]] UniqueMutexHandle AcquireInstallerMutex()
{
    PSECURITY_DESCRIPTOR rawDescriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)",
            SDDL_REVISION_1,
            &rawDescriptor,
            nullptr) ||
        rawDescriptor == nullptr)
    {
        ThrowLastError(
            ExitCode::InternalError,
            "ConvertStringSecurityDescriptorToSecurityDescriptorW(mutex)");
    }
    UniqueLocalMemory descriptor(static_cast<HLOCAL>(rawDescriptor));
    SECURITY_ATTRIBUTES attributes{};
    attributes.nLength = sizeof(attributes);
    attributes.lpSecurityDescriptor = rawDescriptor;
    attributes.bInheritHandle = FALSE;
    UniqueMutexHandle mutex(CreateMutexW(
        &attributes,
        FALSE,
        kMutexName));
    if (!mutex)
    {
        ThrowLastError(ExitCode::IoFailure, "CreateMutexW");
    }
    VerifyInstallerMutexSecurity(mutex.get());
    const DWORD result = WaitForSingleObject(mutex.get(), 30000);
    if (result != WAIT_OBJECT_0 && result != WAIT_ABANDONED)
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Another Comote driver operation owns the installer mutex.");
    }
    return mutex;
}
void CreateExactRootDevice()
{
    UniqueDeviceInfoSet set(SetupDiCreateDeviceInfoList(
        &GUID_DEVCLASS_SYSTEM,
        nullptr));
    if (!set || set.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(
            ExitCode::DeviceCreateFailed,
            "SetupDiCreateDeviceInfoList");
    }
    SP_DEVINFO_DATA data{};
    data.cbSize = sizeof(data);
    if (!SetupDiCreateDeviceInfoW(
            set.get(),
            kRootInstanceId,
            &GUID_DEVCLASS_SYSTEM,
            nullptr,
            nullptr,
            DICD_INHERIT_CLASSDRVS,
            &data))
    {
        ThrowLastError(
            ExitCode::DeviceCreateFailed,
            "SetupDiCreateDeviceInfoW");
    }

    const size_t characters = std::size(kHardwareId) + 1U;
    std::vector<wchar_t> hardwareIds(characters, L'\0');
    std::copy(
        std::begin(kHardwareId),
        std::end(kHardwareId),
        hardwareIds.begin());
    if (!SetupDiSetDeviceRegistryPropertyW(
            set.get(),
            &data,
            SPDRP_HARDWAREID,
            reinterpret_cast<const BYTE*>(hardwareIds.data()),
            static_cast<DWORD>(
                hardwareIds.size() * sizeof(wchar_t))))
    {
        ThrowLastError(
            ExitCode::DeviceCreateFailed,
            "SetupDiSetDeviceRegistryPropertyW");
    }
    if (!SetupDiCallClassInstaller(
            DIF_REGISTERDEVICE,
            set.get(),
            &data))
    {
        ThrowLastError(
            ExitCode::DeviceCreateFailed,
            "DIF_REGISTERDEVICE");
    }
}

[[nodiscard]] fs::path StageExactDriverInf(
    const PackagePaths& package,
    const Manifest& manifest)
{
    std::vector<wchar_t> destination(32768U);
    DWORD required = 0;
    wchar_t* component = nullptr;
    if (!SetupCopyOEMInfW(
            package.inf.c_str(),
            package.directory.c_str(),
            SPOST_PATH,
            SP_COPY_NOOVERWRITE,
            destination.data(),
            static_cast<DWORD>(destination.size()),
            &required,
            &component))
    {
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_EXISTS)
        {
            throw InstallerError(
                ExitCode::Conflict,
                "The exact Phase 2 INF already exists in the Driver Store.");
        }
        SetLastError(error);
        ThrowLastError(
            ExitCode::DriverInstallFailed,
            "SetupCopyOEMInfW");
    }
    if (required == 0 || required > destination.size() ||
        component == nullptr || *component == L'\0')
    {
        throw InstallerError(
            ExitCode::DriverInstallFailed,
            "SetupCopyOEMInfW returned an invalid published INF path.");
    }
    const fs::path published = FullPath(fs::path(destination.data()));
    if (!IsPublishedInfName(published.filename().wstring()) ||
        !EqualsInsensitive(
            published.filename().wstring(),
            std::wstring(component)))
    {
        throw InstallerError(
            ExitCode::DriverInstallFailed,
            "SetupCopyOEMInfW returned an unexpected published INF identity.");
    }
    (void)VerifyInstalledPackage(published, manifest);
    return published;
}

void VerifyPreBindInventory(
    const Manifest& manifest,
    const fs::path& publishedInf)
{
    const Inventory inventory = CollectInventory(manifest);
    if (inventory.roots.size() != 1 ||
        inventory.candidatePublishedInfs.size() != 1 ||
        !inventory.matchingPublishedInf.has_value() ||
        inventory.interfaceInstances.size() != 0 ||
        inventory.service.exists)
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Pre-bind inventory is not the exact unbound Phase 2 topology.");
    }
    const DeviceRecord& root = inventory.roots.front();
    if (!EqualsInsensitive(root.instanceId, kRootInstanceId) ||
        root.hardwareIds.size() != 1 ||
        !EqualsInsensitive(root.hardwareIds.front(), kHardwareId) ||
        !root.serviceName.empty() || !root.publishedInf.empty() ||
        !EqualsInsensitive(root.enumeratorName, L"ROOT") ||
        IsEqualGUID(root.classGuid, GUID_DEVCLASS_SYSTEM) == FALSE ||
        !EqualsInsensitive(
            inventory.matchingPublishedInf->wstring(),
            FullPath(publishedInf).wstring()))
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Pre-bind root identity, class, enumerator, or package is unexpected.");
    }
}

class DriverInfoListGuard final
{
public:
    DriverInfoListGuard(
        const HDEVINFO set,
        SP_DEVINFO_DATA* const device) noexcept
        : set_(set), device_(device)
    {
    }
    ~DriverInfoListGuard()
    {
        if (active_)
        {
            (void)SetupDiDestroyDriverInfoList(
                set_,
                device_,
                SPDIT_COMPATDRIVER);
        }
    }
    DriverInfoListGuard(const DriverInfoListGuard&) = delete;
    DriverInfoListGuard& operator=(const DriverInfoListGuard&) = delete;
    void activate() noexcept
    {
        active_ = true;
    }

private:
    HDEVINFO set_{};
    SP_DEVINFO_DATA* device_{};
    bool active_{};
};

void BindExactDriver(
    const fs::path& publishedInf,
    const Manifest& manifest)
{
    VerifyPreBindInventory(manifest, publishedInf);
    UniqueDeviceInfoSet set(SetupDiGetClassDevsW(
        &GUID_DEVCLASS_SYSTEM,
        nullptr,
        nullptr,
        DIGCF_PRESENT));
    if (!set || set.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(
            ExitCode::DriverInstallFailed,
            "SetupDiGetClassDevsW(bind)");
    }
    SP_DEVINFO_DATA exactDevice{};
    exactDevice.cbSize = sizeof(exactDevice);
    bool foundDevice = false;
    for (DWORD index = 0;; ++index)
    {
        SP_DEVINFO_DATA candidate{};
        candidate.cbSize = sizeof(candidate);
        if (!SetupDiEnumDeviceInfo(set.get(), index, &candidate))
        {
            const DWORD error = GetLastError();
            if (error == ERROR_NO_MORE_ITEMS)
            {
                break;
            }
            SetLastError(error);
            ThrowLastError(
                ExitCode::DriverInstallFailed,
                "SetupDiEnumDeviceInfo(bind)");
        }
        if (!EqualsInsensitive(
                GetDeviceInstanceId(set.get(), candidate),
                kRootInstanceId))
        {
            continue;
        }
        if (foundDevice)
        {
            throw InstallerError(
                ExitCode::Conflict,
                "Duplicate exact root device exists at bind time.");
        }
        const std::vector<std::wstring> ids =
            GetHardwareIds(set.get(), candidate);
        if (ids.size() != 1 ||
            !EqualsInsensitive(ids.front(), kHardwareId) ||
            IsEqualGUID(candidate.ClassGuid, GUID_DEVCLASS_SYSTEM) == FALSE)
        {
            throw InstallerError(
                ExitCode::Conflict,
                "Exact root changed identity before bind.");
        }
        exactDevice = candidate;
        foundDevice = true;
    }
    if (!foundDevice)
    {
        throw InstallerError(
            ExitCode::DriverInstallFailed,
            "Exact root device disappeared before bind.");
    }

    DriverInfoListGuard driverList(set.get(), &exactDevice);
    if (!SetupDiBuildDriverInfoList(
            set.get(),
            &exactDevice,
            SPDIT_COMPATDRIVER))
    {
        ThrowLastError(
            ExitCode::DriverInstallFailed,
            "SetupDiBuildDriverInfoList");
    }
    driverList.activate();

    SP_DRVINFO_DATA_W selected{};
    selected.cbSize = sizeof(selected);
    bool foundDriver = false;
    DWORD compatibleDriverCount = 0;
    for (DWORD index = 0;; ++index)
    {
        SP_DRVINFO_DATA_W candidate{};
        candidate.cbSize = sizeof(candidate);
        if (!SetupDiEnumDriverInfoW(
                set.get(),
                &exactDevice,
                SPDIT_COMPATDRIVER,
                index,
                &candidate))
        {
            const DWORD error = GetLastError();
            if (error == ERROR_NO_MORE_ITEMS)
            {
                break;
            }
            SetLastError(error);
            ThrowLastError(
                ExitCode::DriverInstallFailed,
                "SetupDiEnumDriverInfoW");
        }
        ++compatibleDriverCount;
        DWORD required = 0;
        (void)SetupDiGetDriverInfoDetailW(
            set.get(),
            &exactDevice,
            &candidate,
            nullptr,
            0,
            &required);
        if (required < sizeof(SP_DRVINFO_DETAIL_DATA_W) ||
            GetLastError() != ERROR_INSUFFICIENT_BUFFER)
        {
            throw InstallerError(
                ExitCode::DriverInstallFailed,
                "Driver detail sizing failed.");
        }
        std::vector<unsigned char> storage(required);
        auto* detail = reinterpret_cast<SP_DRVINFO_DETAIL_DATA_W*>(
            storage.data());
        detail->cbSize = sizeof(*detail);
        if (!SetupDiGetDriverInfoDetailW(
                set.get(),
                &exactDevice,
                &candidate,
                detail,
                required,
                nullptr))
        {
            ThrowLastError(
                ExitCode::DriverInstallFailed,
                "SetupDiGetDriverInfoDetailW");
        }
        const fs::path candidateInf = FullPath(
            fs::path(detail->InfFileName));
        if (!EqualsInsensitive(
                candidateInf.wstring(),
                FullPath(publishedInf).wstring()))
        {
            continue;
        }
        if (foundDriver)
        {
            throw InstallerError(
                ExitCode::Conflict,
                "More than one compatible node resolves to the exact INF.");
        }
        selected = candidate;
        foundDriver = true;
    }
    if (!foundDriver || compatibleDriverCount != 1)
    {
        throw InstallerError(
            ExitCode::Conflict,
            "The exact INF is not the sole compatible driver node.");
    }
    if (!SetupDiSetSelectedDriverW(
            set.get(),
            &exactDevice,
            &selected))
    {
        ThrowLastError(
            ExitCode::DriverInstallFailed,
            "SetupDiSetSelectedDriverW");
    }
    BOOL rebootRequired = FALSE;
    if (!DiInstallDevice(
            nullptr,
            set.get(),
            &exactDevice,
            &selected,
            0,
            &rebootRequired))
    {
        ThrowLastError(
            ExitCode::DriverInstallFailed,
            "DiInstallDevice");
    }
    if (rebootRequired != FALSE)
    {
        throw InstallerError(
            ExitCode::RebootRequired,
            "Exact driver binding requested a reboot.");
    }
}
[[nodiscard]] bool UninstallExactRootDevice()
{
    UniqueDeviceInfoSet set(SetupDiGetClassDevsW(
        nullptr,
        nullptr,
        nullptr,
        DIGCF_ALLCLASSES));
    if (!set || set.get() == INVALID_HANDLE_VALUE)
    {
        ThrowLastError(
            ExitCode::DeviceRemoveFailed,
            "SetupDiGetClassDevsW(remove)");
    }
    bool found = false;
    for (DWORD index = 0;; ++index)
    {
        SP_DEVINFO_DATA data{};
        data.cbSize = sizeof(data);
        if (!SetupDiEnumDeviceInfo(set.get(), index, &data))
        {
            const DWORD error = GetLastError();
            if (error == ERROR_NO_MORE_ITEMS)
            {
                break;
            }
            SetLastError(error);
            ThrowLastError(
                ExitCode::DeviceRemoveFailed,
                "SetupDiEnumDeviceInfo(remove)");
        }
        const std::wstring instance = GetDeviceInstanceId(set.get(), data);
        if (!EqualsInsensitive(instance, kRootInstanceId))
        {
            continue;
        }
        const std::vector<std::wstring> ids = GetHardwareIds(set.get(), data);
        if (ids.size() != 1 ||
            !EqualsInsensitive(ids.front(), kHardwareId) ||
            !EqualsInsensitive(
                GetRegistryStringProperty(
                    set.get(), data, SPDRP_ENUMERATOR_NAME),
                L"ROOT") ||
            IsEqualGUID(data.ClassGuid, GUID_DEVCLASS_SYSTEM) == FALSE)
        {
            throw InstallerError(
                ExitCode::Conflict,
                "Exact root instance has an unexpected hardware ID.");
        }
        if (found)
        {
            throw InstallerError(
                ExitCode::Conflict,
                "Duplicate exact root device instances exist.");
        }
        BOOL rebootRequired = FALSE;
        if (!DiUninstallDevice(
                nullptr,
                set.get(),
                &data,
                0,
                &rebootRequired))
        {
            ThrowLastError(
                ExitCode::DeviceRemoveFailed,
                "DiUninstallDevice");
        }
        if (rebootRequired != FALSE)
        {
            throw InstallerError(
                ExitCode::RebootRequired,
                "Device removal requested a reboot.");
        }
        found = true;
    }
    return found;
}

void WaitForRootAbsence()
{
    for (unsigned int attempt = 0; attempt < 40U; ++attempt)
    {
        if (EnumeratePhase2Roots(false).empty())
        {
            return;
        }
        Sleep(250);
    }
    throw InstallerError(
        ExitCode::DeviceRemoveFailed,
        "Exact Phase 2 root remained after removal.");
}

void UninstallExactDriverPackage(const fs::path& publishedInf)
{
    if (!IsPublishedInfName(publishedInf.filename().wstring()))
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Refusing to remove an invalid published INF name.");
    }
    BOOL rebootRequired = FALSE;
    if (!DiUninstallDriverW(
            nullptr,
            publishedInf.c_str(),
            0,
            &rebootRequired))
    {
        ThrowLastError(
            ExitCode::PackageRemoveFailed,
            "DiUninstallDriverW");
    }
    if (rebootRequired != FALSE)
    {
        throw InstallerError(
            ExitCode::RebootRequired,
            "Driver package removal requested a reboot.");
    }
}

void DeleteVerifiedOrphanedService(const fs::path& expectedSys)
{
    UniqueServiceHandle manager(OpenSCManagerW(
        nullptr,
        nullptr,
        SC_MANAGER_CONNECT));
    if (!manager)
    {
        ThrowLastError(
            ExitCode::ServiceCleanupFailed,
            "OpenSCManagerW(delete)");
    }
    UniqueServiceHandle service(OpenServiceW(
        manager.get(),
        kServiceName,
        SERVICE_QUERY_STATUS |
            SERVICE_QUERY_CONFIG |
            SERVICE_CHANGE_CONFIG |
            SERVICE_STOP |
            DELETE));
    if (!service)
    {
        const DWORD error = GetLastError();
        if (error == ERROR_SERVICE_DOES_NOT_EXIST)
        {
            return;
        }
        SetLastError(error);
        ThrowLastError(
            ExitCode::ServiceCleanupFailed,
            "OpenServiceW(delete)");
    }
    ServiceRecord record = QueryServiceRecordFromHandle(
        service.get(),
        ExitCode::ServiceCleanupFailed);
    if (record.serviceType != SERVICE_KERNEL_DRIVER ||
        (record.startType != SERVICE_DEMAND_START &&
         record.startType != SERVICE_DISABLED) ||
        !EqualsInsensitive(
            NormalizeServiceBinaryPath(record.binaryPath),
            expectedSys.wstring()))
    {
        throw InstallerError(
            ExitCode::ServiceCleanupFailed,
            "Refusing to change a mismatched kernel driver service.");
    }

    if (record.state != SERVICE_STOPPED)
    {
        // Disable only after the exact service identity was verified through
        // this same handle. If the kernel driver cannot unload immediately,
        // the protected receipt will require a reboot and resume cleanup.
        if (!ChangeServiceConfigW(
                service.get(),
                SERVICE_NO_CHANGE,
                SERVICE_DISABLED,
                SERVICE_NO_CHANGE,
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                nullptr))
        {
            ThrowLastError(
                ExitCode::ServiceCleanupFailed,
                "ChangeServiceConfigW(disable exact service)");
        }
        if (record.state != SERVICE_STOP_PENDING)
        {
            SERVICE_STATUS controlStatus{};
            if (!ControlService(
                    service.get(),
                    SERVICE_CONTROL_STOP,
                    &controlStatus))
            {
                const DWORD error = GetLastError();
                if (error != ERROR_SERVICE_NOT_ACTIVE &&
                    error != ERROR_SERVICE_CANNOT_ACCEPT_CTRL)
                {
                    SetLastError(error);
                    ThrowLastError(
                        ExitCode::ServiceCleanupFailed,
                        "ControlService(stop exact service)");
                }
            }
        }
        for (unsigned int attempt = 0; attempt < 40U; ++attempt)
        {
            SERVICE_STATUS_PROCESS status{};
            DWORD returned = 0;
            if (!QueryServiceStatusEx(
                    service.get(),
                    SC_STATUS_PROCESS_INFO,
                    reinterpret_cast<LPBYTE>(&status),
                    sizeof(status),
                    &returned))
            {
                ThrowLastError(
                    ExitCode::ServiceCleanupFailed,
                    "QueryServiceStatusEx(wait-stop)");
            }
            if (status.dwCurrentState == SERVICE_STOPPED)
            {
                record.state = SERVICE_STOPPED;
                break;
            }
            Sleep(250);
        }
        if (record.state != SERVICE_STOPPED)
        {
            throw InstallerError(
                ExitCode::RebootRequired,
                "The exact kernel service was disabled but requires a VM reboot to unload.");
        }
    }

    record = QueryServiceRecordFromHandle(
        service.get(),
        ExitCode::ServiceCleanupFailed);
    if (record.state != SERVICE_STOPPED ||
        record.serviceType != SERVICE_KERNEL_DRIVER ||
        (record.startType != SERVICE_DEMAND_START &&
         record.startType != SERVICE_DISABLED) ||
        !EqualsInsensitive(
            NormalizeServiceBinaryPath(record.binaryPath),
            expectedSys.wstring()))
    {
        throw InstallerError(
            ExitCode::ServiceCleanupFailed,
            "Exact service identity changed before deletion.");
    }
    if (!DeleteService(service.get()))
    {
        ThrowLastError(
            ExitCode::ServiceCleanupFailed,
            "DeleteService");
    }
    service.reset();
    for (unsigned int attempt = 0; attempt < 40U; ++attempt)
    {
        UniqueServiceHandle probe(OpenServiceW(
            manager.get(),
            kServiceName,
            SERVICE_QUERY_STATUS));
        if (!probe)
        {
            const DWORD error = GetLastError();
            if (error == ERROR_SERVICE_DOES_NOT_EXIST)
            {
                return;
            }
            if (error != ERROR_SERVICE_MARKED_FOR_DELETE)
            {
                SetLastError(error);
                ThrowLastError(
                    ExitCode::ServiceCleanupFailed,
                    "OpenServiceW(wait-delete)");
            }
        }
        Sleep(250);
    }
    throw InstallerError(
        ExitCode::ServiceCleanupFailed,
        "Orphaned service remained after DeleteService.");
}
[[nodiscard]] fs::path VerifyOrphanedServiceBinary(
    const ServiceRecord& service,
    const Manifest& manifest)
{
    if (!service.exists)
    {
        return {};
    }
    const fs::path binary = FullPath(
        fs::path(NormalizeServiceBinaryPath(service.binaryPath)));
    const std::wstring normalized = binary.wstring();
    const std::wstring expectedPrefix =
        (WindowsDirectory() /
         L"System32" /
         L"DriverStore" /
         L"FileRepository" /
         L"comotevirtualhidphase2.inf_amd64_").wstring();
    if (!StartsWithInsensitive(normalized, expectedPrefix) ||
        !EndsWithInsensitive(normalized, L"\\ComoteVirtualHidPhase2.sys"))
    {
        throw InstallerError(
            ExitCode::ServiceCleanupFailed,
            "Orphaned service binary is outside the exact Phase 2 Driver Store path.");
    }
    const fs::path directory = binary.parent_path();
    const fs::path catalog = directory / kCatName;
    const LocalFileMetadata binaryMetadata =
        InspectLocalRegularFile(binary, directory);
    const LocalFileMetadata catalogMetadata =
        InspectLocalRegularFile(catalog, directory);
    if (binaryMetadata.size != manifest.sysSize ||
        catalogMetadata.size != manifest.catSize ||
        Sha256File(binary) != manifest.sysSha256 ||
        Sha256File(catalog) != manifest.catSha256)
    {
        throw InstallerError(
            ExitCode::PackageHashMismatch,
            "Orphaned service package does not match the pinned manifest.");
    }
    VerifyAuthenticodeFile(catalog);
    VerifyExactCatalogMemberAndTrust(catalog, binary);
    return binary;
}

void ValidateStateAgainstManifest(
    const InstallerState& state,
    const Manifest& manifest)
{
    const fs::path expectedStage = FullPath(DefaultStagingDirectory());
    const bool validStage = state.stagePath == L"-" ||
        EqualsInsensitive(
            FullPath(fs::path(state.stagePath)).wstring(),
            expectedStage.wstring());
    const bool validTransaction =
        (state.status == "Installing" && state.operation == "Install") ||
        (state.status == "Installed" && state.operation == "Install" &&
         !state.needsReboot) ||
        (state.status == "Removing" && state.operation == "Remove") ||
        state.status == "RecoveryRequired";
    if (state.manifestSha256 != manifest.manifestSha256 ||
        state.infSha256 != manifest.infSha256 ||
        state.catSha256 != manifest.catSha256 ||
        state.sysSha256 != manifest.sysSha256 ||
        !EqualsInsensitive(state.hardwareId, manifest.hardwareId) ||
        !EqualsInsensitive(state.rootInstanceId, manifest.rootInstanceId) ||
        !EqualsInsensitive(state.serviceName, manifest.serviceName) ||
        !validStage || !validTransaction)
    {
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "The pinned manifest does not match the protected installer state.");
    }
}

void ClearRecordedStaging(const InstallerState& state)
{
    if (state.stagePath == L"-")
    {
        return;
    }
    const fs::path stage = FullPath(fs::path(state.stagePath));
    if (!EqualsInsensitive(
            stage.wstring(),
            FullPath(DefaultStagingDirectory()).wstring()))
    {
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Refusing to clean an unexpected staging path.");
    }
    if (fs::exists(stage))
    {
        ClearExactStagingDirectory(stage);
    }
}
void CleanupExactInstallation(
    const Manifest& manifest,
    const fs::path& statePath,
    InstallerState& state)
{
    ValidateStateAgainstManifest(state, manifest);
    Inventory inventory = CollectInventory(manifest);
    if (IsInventoryClean(inventory))
    {
        ClearRecordedStaging(state);
        DeleteState(statePath);
        return;
    }
    if (inventory.roots.size() > 1 ||
        inventory.candidatePublishedInfs.size() > 1 ||
        (!inventory.candidatePublishedInfs.empty() &&
         !inventory.matchingPublishedInf.has_value()))
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Exact cleanup inventory is ambiguous.");
    }
    for (const DeviceRecord& root : inventory.roots)
    {
        if (!EqualsInsensitive(root.instanceId, kRootInstanceId) ||
            root.hardwareIds.size() != 1 ||
            !EqualsInsensitive(root.hardwareIds.front(), kHardwareId) ||
            !EqualsInsensitive(root.enumeratorName, L"ROOT") ||
            IsEqualGUID(root.classGuid, GUID_DEVCLASS_SYSTEM) == FALSE ||
            (!root.serviceName.empty() &&
             !EqualsInsensitive(root.serviceName, kServiceName)))
        {
            throw InstallerError(
                ExitCode::Conflict,
                "A Phase 2 hardware ID is attached to an unexpected root instance.");
        }
    }
    for (const std::wstring& interfaceInstance :
         inventory.interfaceInstances)
    {
        if (!EqualsInsensitive(interfaceInstance, kRootInstanceId))
        {
            throw InstallerError(
                ExitCode::Conflict,
                "A Phase 2 interface belongs to an unexpected device.");
        }
    }

    std::optional<InstalledPackage> installedPackage;
    fs::path expectedSys;
    if (inventory.matchingPublishedInf.has_value())
    {
        installedPackage = VerifyInstalledPackage(
            inventory.matchingPublishedInf.value(),
            manifest);
        if (state.publishedInf != L"-" &&
            !EqualsInsensitive(
                state.publishedInf,
                installedPackage->publishedInf.filename().wstring()))
        {
            throw InstallerError(
                ExitCode::Conflict,
                "Recorded and discovered published INF names disagree.");
        }
        expectedSys = installedPackage->sys;
        state.publishedInf = installedPackage->publishedInf.filename().wstring();
    }
    else if (inventory.service.exists)
    {
        expectedSys = VerifyOrphanedServiceBinary(
            inventory.service,
            manifest);
    }
    if (inventory.service.exists && expectedSys.empty())
    {
        throw InstallerError(
            ExitCode::ServiceCleanupFailed,
            "Service cleanup has no verified Driver Store binary.");
    }

    state.status = "Removing";
    state.operation = "Remove";
    state.needsReboot = false;
    state.bootId = CurrentBootId();
    WriteStateAtomically(statePath, state);

    if (!inventory.roots.empty())
    {
        (void)UninstallExactRootDevice();
        WaitForRootAbsence();
    }
    // Remove the same-handle-verified service while the pinned Driver Store
    // SYS and CAT still exist. A crash after this point leaves a verifiable
    // package with no service, never an unverifiable service with no files.
    if (QueryDriverService().exists)
    {
        DeleteVerifiedOrphanedService(expectedSys);
    }
    if (installedPackage.has_value())
    {
        UninstallExactDriverPackage(installedPackage->publishedInf);
        for (unsigned int attempt = 0; attempt < 40U; ++attempt)
        {
            if (FindPhase2PublishedInfs().empty())
            {
                break;
            }
            Sleep(250);
        }
        if (!FindPhase2PublishedInfs().empty())
        {
            throw InstallerError(
                ExitCode::PackageRemoveFailed,
                "Published Phase 2 INF remained after removal.");
        }
    }

    inventory = CollectInventory(manifest);
    if (!IsInventoryClean(inventory))
    {
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Phase 2 inventory is not clean after exact removal.");
    }
    ClearRecordedStaging(state);
    DeleteState(statePath);
}

[[nodiscard]] InstallerState NewInstallingState(
    const Manifest& manifest,
    const std::uint32_t bootId)
{
    return InstallerState{
        "Installing",
        "Install",
        manifest.manifestSha256,
        manifest.hardwareId,
        manifest.rootInstanceId,
        manifest.serviceName,
        L"-",
        FullPath(DefaultStagingDirectory()).wstring(),
        manifest.infSha256,
        manifest.catSha256,
        manifest.sysSha256,
        bootId,
        false};
}

void RecordRebootRequired(
    const fs::path& statePath,
    InstallerState& state,
    const std::string& status,
    const std::string& operation)
{
    state.status = status;
    state.operation = operation;
    state.needsReboot = true;
    state.bootId = CurrentBootId();
    WriteStateAtomically(statePath, state);
}

void FinishPriorTransactionBeforeInstall(
    const Manifest& manifest,
    const fs::path& statePath,
    InstallerState& state)
{
    ValidateStateAgainstManifest(state, manifest);
    const std::uint32_t currentBoot = CurrentBootId();
    if (state.needsReboot && state.bootId == currentBoot)
    {
        throw InstallerError(
            ExitCode::RebootRequired,
            "The recorded driver transaction still requires a VM reboot.");
    }
    Inventory inventory = CollectInventory(manifest);
    if (state.status == "Installed" && !state.needsReboot)
    {
        try
        {
            if (state.stagePath == L"-")
            {
                throw InstallerError(
                    ExitCode::RecoveryRequired,
                    "Installed receipt has no protected staging path.");
            }
            (void)ValidateProtectedStagingPackage(
                fs::path(state.stagePath),
                manifest);
            (void)VerifyInstalledState(
                inventory,
                manifest,
                std::optional<InstallerState>(state));
        }
        catch (const InstallerError& error)
        {
            throw InstallerError(
                ExitCode::RecoveryRequired,
                "Recorded installation is not healthy: " +
                    std::string(error.what()));
        }
        throw InstallerError(
            ExitCode::AlreadyInstalled,
            "Comote Phase 2 is already installed and healthy.");
    }
    if (state.operation == "Install")
    {
        try
        {
            if (state.stagePath == L"-")
            {
                throw InstallerError(
                    ExitCode::RecoveryRequired,
                    "Interrupted install has no protected staging path.");
            }
            (void)ValidateProtectedStagingPackage(
                fs::path(state.stagePath),
                manifest);
            state.needsReboot = false;
            state.bootId = currentBoot;
            state.status = "Installed";
            state.operation = "Install";
            (void)VerifyInstalledState(
                inventory,
                manifest,
                std::nullopt);
            WriteStateAtomically(statePath, state);
            (void)VerifyInstalledState(
                inventory,
                manifest,
                std::optional<InstallerState>(state));
            throw InstallerError(
                ExitCode::AlreadyInstalled,
                "Comote Phase 2 completed an interrupted installation.");
        }
        catch (const InstallerError& error)
        {
            if (error.code() == ExitCode::AlreadyInstalled)
            {
                throw;
            }
        }
    }
    state.status = "RecoveryRequired";
    state.operation = "Remove";
    state.needsReboot = false;
    state.bootId = currentBoot;
    WriteStateAtomically(statePath, state);
    try
    {
        CleanupExactInstallation(manifest, statePath, state);
    }
    catch (const InstallerError& error)
    {
        if (error.code() == ExitCode::RebootRequired)
        {
            RecordRebootRequired(
                statePath,
                state,
                "Removing",
                "Remove");
        }
        throw;
    }
}

[[nodiscard]] ExitCode Install(
    const Manifest& manifest,
    const PackagePaths& sourcePackage,
    const fs::path& statePath)
{
    std::optional<InstallerState> existing = LoadState(statePath);
    if (existing.has_value())
    {
        FinishPriorTransactionBeforeInstall(
            manifest,
            statePath,
            existing.value());
    }
    Inventory inventory = CollectInventory(manifest);
    if (!IsInventoryClean(inventory))
    {
        throw InstallerError(
            ExitCode::Conflict,
            "Phase 2 device, package, service, or interface exists without a protected receipt.");
    }

    InstallerState state = NewInstallingState(
        manifest,
        CurrentBootId());
    WriteStateAtomically(statePath, state);
    try
    {
        const PackagePaths staged =
            StageValidatedPackage(sourcePackage, manifest);
        CreateExactRootDevice();
        const fs::path published =
            StageExactDriverInf(staged, manifest);
        state.publishedInf = published.filename().wstring();
        WriteStateAtomically(statePath, state);
        BindExactDriver(published, manifest);

        std::optional<InstallerError> lastError;
        for (unsigned int attempt = 0; attempt < 60U; ++attempt)
        {
            try
            {
                inventory = CollectInventory(manifest);
                (void)VerifyInstalledState(
                    inventory,
                    manifest,
                    std::nullopt);
                lastError.reset();
                break;
            }
            catch (const InstallerError& error)
            {
                lastError = error;
                Sleep(250);
            }
        }
        if (lastError.has_value())
        {
            throw InstallerError(
                ExitCode::VerificationFailed,
                "Installed device did not become healthy: " +
                    std::string(lastError->what()));
        }
        state.status = "Installed";
        state.operation = "Install";
        state.needsReboot = false;
        state.bootId = CurrentBootId();
        WriteStateAtomically(statePath, state);
        inventory = CollectInventory(manifest);
        (void)VerifyInstalledState(
            inventory,
            manifest,
            std::optional<InstallerState>(state));
        return ExitCode::Success;
    }
    catch (const InstallerError& error)
    {
        const std::exception_ptr original = std::current_exception();
        if (error.code() == ExitCode::RebootRequired)
        {
            RecordRebootRequired(
                statePath,
                state,
                "Installing",
                "Install");
            std::rethrow_exception(original);
        }
        state.status = "RecoveryRequired";
        state.operation = "Remove";
        state.needsReboot = false;
        state.bootId = CurrentBootId();
        WriteStateAtomically(statePath, state);
        try
        {
            CleanupExactInstallation(manifest, statePath, state);
        }
        catch (const InstallerError& cleanupError)
        {
            if (cleanupError.code() == ExitCode::RebootRequired)
            {
                RecordRebootRequired(
                    statePath,
                    state,
                    "Removing",
                    "Remove");
                throw;
            }
            state.status = "RecoveryRequired";
            state.operation = "Remove";
            state.needsReboot = false;
            try
            {
                WriteStateAtomically(statePath, state);
            }
            catch (...)
            {
            }
            throw InstallerError(
                ExitCode::RecoveryRequired,
                "Install failed and exact rollback did not complete.");
        }
        std::rethrow_exception(original);
    }
    catch (...)
    {
        state.status = "RecoveryRequired";
        state.operation = "Remove";
        state.needsReboot = false;
        try
        {
            WriteStateAtomically(statePath, state);
        }
        catch (...)
        {
        }
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Unexpected install failure requires exact recovery.");
    }
}

[[nodiscard]] ExitCode Remove(
    const Manifest& manifest,
    const fs::path& statePath)
{
    std::optional<InstallerState> state = LoadState(statePath);
    const Inventory inventory = CollectInventory(manifest);
    if (!state.has_value())
    {
        if (IsInventoryClean(inventory))
        {
            throw InstallerError(
                ExitCode::NotInstalled,
                "Comote Phase 2 is not installed.");
        }
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Phase 2 state exists without the protected installer receipt.");
    }
    ValidateStateAgainstManifest(state.value(), manifest);
    const std::uint32_t currentBoot = CurrentBootId();
    if (state->needsReboot && state->bootId == currentBoot)
    {
        throw InstallerError(
            ExitCode::RebootRequired,
            "Removal still requires a VM reboot.");
    }
    state->status = "Removing";
    state->operation = "Remove";
    state->needsReboot = false;
    state->bootId = currentBoot;
    WriteStateAtomically(statePath, state.value());
    try
    {
        CleanupExactInstallation(manifest, statePath, state.value());
    }
    catch (const InstallerError& error)
    {
        if (error.code() == ExitCode::RebootRequired)
        {
            RecordRebootRequired(
                statePath,
                state.value(),
                "Removing",
                "Remove");
            throw;
        }
        state->status = "RecoveryRequired";
        state->operation = "Remove";
        state->needsReboot = false;
        try
        {
            WriteStateAtomically(statePath, state.value());
        }
        catch (...)
        {
        }
        throw;
    }
    return ExitCode::Success;
}

[[nodiscard]] ExitCode Status(
    const Manifest& manifest,
    const fs::path& statePath)
{
    const std::optional<InstallerState> state = LoadState(statePath);
    const Inventory inventory = CollectInventory(manifest);
    if (!state.has_value())
    {
        if (IsInventoryClean(inventory))
        {
            throw InstallerError(
                ExitCode::NotInstalled,
                "Comote Phase 2 is not installed.");
        }
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Phase 2 inventory exists without a protected receipt.");
    }
    ValidateStateAgainstManifest(state.value(), manifest);
    if (state->needsReboot)
    {
        throw InstallerError(
            ExitCode::RebootRequired,
            "The protected receipt records a pending reboot.");
    }
    if (state->status != "Installed" || state->operation != "Install")
    {
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Installer receipt records an unfinished transaction.");
    }
    try
    {
        if (state->stagePath == L"-")
        {
            throw InstallerError(
                ExitCode::RecoveryRequired,
                "Installed receipt has no protected staging path.");
        }
        (void)ValidateProtectedStagingPackage(
            fs::path(state->stagePath),
            manifest);
        (void)VerifyInstalledState(
            inventory,
            manifest,
            state);
    }
    catch (const InstallerError& error)
    {
        throw InstallerError(
            ExitCode::RecoveryRequired,
            "Installed state verification failed: " +
                std::string(error.what()));
    }
    return ExitCode::Success;
}
struct Arguments
{
    std::wstring command;
    fs::path manifest;
    std::optional<fs::path> package;
    fs::path state;
};

[[nodiscard]] Arguments ParseArguments(const int argc, wchar_t* argv[])
{
    if (argc < 2)
    {
        throw InstallerError(
            ExitCode::Usage,
            "Missing command.");
    }
    Arguments arguments{};
    arguments.command = argv[1];
    arguments.state = DefaultStatePath();
    bool manifestSeen = false;
    bool packageSeen = false;
    bool stateSeen = false;
    for (int index = 2; index < argc; ++index)
    {
        const std::wstring option = argv[index];
        if (index + 1 >= argc)
        {
            throw InstallerError(
                ExitCode::Usage,
                "Option is missing its value.");
        }
        const fs::path value = argv[++index];
        if (option == L"--manifest" && !manifestSeen)
        {
            arguments.manifest = value;
            manifestSeen = true;
        }
        else if (option == L"--package" && !packageSeen)
        {
            arguments.package = value;
            packageSeen = true;
        }
        else if (option == L"--state" && !stateSeen)
        {
#if COMOTE_INSTALLER_VM_TEST
            arguments.state = value;
            stateSeen = true;
#else
            throw InstallerError(
                ExitCode::Usage,
                "--state is disabled in production installers.");
#endif
        }
        else
        {
            throw InstallerError(
                ExitCode::Usage,
                "Unknown or duplicate option.");
        }
    }
    if (!manifestSeen ||
        (arguments.command != L"install" &&
         arguments.command != L"remove" &&
         arguments.command != L"status") ||
        (arguments.command == L"install" && !packageSeen) ||
        (arguments.command != L"install" && packageSeen))
    {
        throw InstallerError(
            ExitCode::Usage,
            "Usage: ComoteDriverInstaller.exe install --package <dir> --manifest <file> | remove --manifest <file> | status --manifest <file>");
    }
    return arguments;
}

[[nodiscard]] std::string EscapeResultMessage(std::string value)
{
    for (char& ch : value)
    {
        if (ch == '\r' || ch == '\n' || ch == '"')
        {
            ch = ' ';
        }
    }
    return value;
}

void PrintResult(
    const ExitCode code,
    const std::string& state,
    const std::string& message)
{
    std::cout << "COMOTE_INSTALLER_RESULT code="
              << static_cast<int>(code)
              << " state=" << state
              << " message=\"" << EscapeResultMessage(message)
              << "\"" << std::endl;
}
} // namespace

int wmain(const int argc, wchar_t* argv[])
{
    try
    {
        const Arguments arguments = ParseArguments(argc, argv);
        VerifySupportedPlatform();
        if (!IsAdministrator())
        {
            throw InstallerError(
                ExitCode::NotElevated,
                "Installer commands require an elevated administrator token.");
        }
        UniqueMutexHandle mutex = AcquireInstallerMutex();
        (void)mutex;
        const Manifest manifest = LoadManifest(arguments.manifest);

        ExitCode result = ExitCode::InternalError;
        std::string state;
        if (arguments.command == L"install")
        {
            const PackagePaths package = ValidatePackage(
                arguments.package.value(),
                manifest);
            result = Install(manifest, package, arguments.state);
            state = "installed";
        }
        else if (arguments.command == L"remove")
        {
            result = Remove(manifest, arguments.state);
            state = "removed";
        }
        else
        {
            result = Status(manifest, arguments.state);
            state = "installed";
        }
        PrintResult(result, state, "OK");
        return static_cast<int>(result);
    }
    catch (const InstallerError& error)
    {
        std::string state = "error";
        if (error.code() == ExitCode::NotInstalled)
        {
            state = "not-installed";
        }
        else if (error.code() == ExitCode::AlreadyInstalled)
        {
            state = "installed";
        }
        else if (error.code() == ExitCode::RecoveryRequired ||
                 error.code() == ExitCode::RebootRequired)
        {
            state = "recovery-required";
        }
        PrintResult(error.code(), state, error.what());
        return static_cast<int>(error.code());
    }
    catch (const std::exception& error)
    {
        PrintResult(
            ExitCode::InternalError,
            "error",
            error.what());
        return static_cast<int>(ExitCode::InternalError);
    }
    catch (...)
    {
        PrintResult(
            ExitCode::InternalError,
            "error",
            "Unknown internal error.");
        return static_cast<int>(ExitCode::InternalError);
    }
}