#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <SetupAPI.h>
#include <stdio.h>
#include <wchar.h>

#include "..\ComoteVirtualHidProtocol.h"

static
void
PrintLastError(
    _In_z_ const wchar_t* Operation)
{
    fwprintf(
        stderr,
        L"%ls failed. Win32 error=%lu\n",
        Operation,
        GetLastError());
}

static
HANDLE
OpenComoteDevice(void)
{
    HDEVINFO deviceInfoSet;
    SP_DEVICE_INTERFACE_DATA interfaceData;
    SP_DEVICE_INTERFACE_DATA secondInterfaceData;
    PSP_DEVICE_INTERFACE_DETAIL_DATA_W detailData;
    DWORD requiredSize;
    HANDLE deviceHandle;

    deviceInfoSet = SetupDiGetClassDevsW(
        &GUID_DEVINTERFACE_COMOTE_VIRTUAL_HID_PHASE2,
        NULL,
        NULL,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (deviceInfoSet == INVALID_HANDLE_VALUE)
    {
        PrintLastError(L"SetupDiGetClassDevsW");
        return INVALID_HANDLE_VALUE;
    }

    ZeroMemory(
        &interfaceData,
        sizeof(interfaceData));
    interfaceData.cbSize = (DWORD)sizeof(interfaceData);
    if (!SetupDiEnumDeviceInterfaces(
            deviceInfoSet,
            NULL,
            &GUID_DEVINTERFACE_COMOTE_VIRTUAL_HID_PHASE2,
            0,
            &interfaceData))
    {
        PrintLastError(L"SetupDiEnumDeviceInterfaces");
        SetupDiDestroyDeviceInfoList(deviceInfoSet);
        return INVALID_HANDLE_VALUE;
    }

    ZeroMemory(
        &secondInterfaceData,
        sizeof(secondInterfaceData));
    secondInterfaceData.cbSize =
        (DWORD)sizeof(secondInterfaceData);
    if (SetupDiEnumDeviceInterfaces(
            deviceInfoSet,
            NULL,
            &GUID_DEVINTERFACE_COMOTE_VIRTUAL_HID_PHASE2,
            1,
            &secondInterfaceData) ||
        GetLastError() != ERROR_NO_MORE_ITEMS)
    {
        fwprintf(
            stderr,
            L"Expected exactly one Comote Phase 2 device interface.\n");
        SetupDiDestroyDeviceInfoList(deviceInfoSet);
        SetLastError(ERROR_DUPLICATE_TAG);
        return INVALID_HANDLE_VALUE;
    }

    requiredSize = 0;
    (void)SetupDiGetDeviceInterfaceDetailW(
        deviceInfoSet,
        &interfaceData,
        NULL,
        0,
        &requiredSize,
        NULL);
    if (requiredSize <
            (DWORD)sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER)
    {
        PrintLastError(L"SetupDiGetDeviceInterfaceDetailW(size)");
        SetupDiDestroyDeviceInfoList(deviceInfoSet);
        return INVALID_HANDLE_VALUE;
    }

    detailData = (PSP_DEVICE_INTERFACE_DETAIL_DATA_W)HeapAlloc(
        GetProcessHeap(),
        HEAP_ZERO_MEMORY,
        requiredSize);
    if (detailData == NULL)
    {
        fwprintf(
            stderr,
            L"Unable to allocate the device interface path buffer.\n");
        SetupDiDestroyDeviceInfoList(deviceInfoSet);
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return INVALID_HANDLE_VALUE;
    }
    detailData->cbSize = (DWORD)sizeof(*detailData);

    if (!SetupDiGetDeviceInterfaceDetailW(
            deviceInfoSet,
            &interfaceData,
            detailData,
            requiredSize,
            NULL,
            NULL))
    {
        PrintLastError(L"SetupDiGetDeviceInterfaceDetailW");
        HeapFree(
            GetProcessHeap(),
            0,
            detailData);
        SetupDiDestroyDeviceInfoList(deviceInfoSet);
        return INVALID_HANDLE_VALUE;
    }

    deviceHandle = CreateFileW(
        detailData->DevicePath,
        GENERIC_READ | GENERIC_WRITE,
        0,
        NULL,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        NULL);
    if (deviceHandle == INVALID_HANDLE_VALUE)
    {
        PrintLastError(L"CreateFileW");
    }

    HeapFree(
        GetProcessHeap(),
        0,
        detailData);
    SetupDiDestroyDeviceInfoList(deviceInfoSet);
    return deviceHandle;
}

static
BOOL
SendIoctl(
    _In_ HANDLE DeviceHandle,
    _In_ DWORD ControlCode,
    _In_reads_bytes_opt_(InputSize) const void* Input,
    _In_ DWORD InputSize,
    _Out_writes_bytes_opt_(OutputSize) void* Output,
    _In_ DWORD OutputSize,
    _Out_opt_ DWORD* BytesReturned)
{
    DWORD localBytesReturned;
    BOOL result;

    localBytesReturned = 0;
    result = DeviceIoControl(
        DeviceHandle,
        ControlCode,
        (LPVOID)Input,
        InputSize,
        Output,
        OutputSize,
        &localBytesReturned,
        NULL);
    if (!result)
    {
        PrintLastError(L"DeviceIoControl");
    }
    if (BytesReturned != NULL)
    {
        *BytesReturned = localBytesReturned;
    }
    return result;
}

static
void
InitializeHeader(
    _Out_ PCOMOTE_COMMAND_HEADER Header,
    _In_ ULONG Size,
    _In_ ULONG Sequence)
{
    ZeroMemory(
        Header,
        sizeof(*Header));
    Header->Size = Size;
    Header->Magic = COMOTE_PROTOCOL_MAGIC;
    Header->ProtocolVersion = COMOTE_PROTOCOL_VERSION;
    Header->Sequence = Sequence;
}

_Success_(return != FALSE)
static
BOOL
ParseUnsignedByte(
    _In_z_ const wchar_t* Text,
    _Out_ UCHAR* Value)
{
    wchar_t* end;
    unsigned long parsed;

    end = NULL;
    parsed = wcstoul(
        Text,
        &end,
        0);
    if (end == Text ||
        *end != L'\0' ||
        parsed > 0xFFUL)
    {
        return FALSE;
    }

    *Value = (UCHAR)parsed;
    return TRUE;
}

_Success_(return != FALSE)
static
BOOL
ParseSignedRange(
    _In_z_ const wchar_t* Text,
    _In_ long Minimum,
    _In_ long Maximum,
    _Out_ long* Value)
{
    wchar_t* end;
    long parsed;

    end = NULL;
    parsed = wcstol(
        Text,
        &end,
        0);
    if (end == Text ||
        *end != L'\0' ||
        parsed < Minimum ||
        parsed > Maximum)
    {
        return FALSE;
    }

    *Value = parsed;
    return TRUE;
}

static
BOOL
RunParserSelfTest(void)
{
    long parsed;

    parsed = 0;
    if (ParseSignedRange(
            L"-32768",
            -COMOTE_MOUSE_MAX_DELTA,
            COMOTE_MOUSE_MAX_DELTA,
            &parsed) ||
        !ParseSignedRange(
            L"-32767",
            -COMOTE_MOUSE_MAX_DELTA,
            COMOTE_MOUSE_MAX_DELTA,
            &parsed) ||
        parsed != -COMOTE_MOUSE_MAX_DELTA ||
        !ParseSignedRange(
            L"32767",
            -COMOTE_MOUSE_MAX_DELTA,
            COMOTE_MOUSE_MAX_DELTA,
            &parsed) ||
        parsed != COMOTE_MOUSE_MAX_DELTA ||
        ParseSignedRange(
            L"32768",
            -COMOTE_MOUSE_MAX_DELTA,
            COMOTE_MOUSE_MAX_DELTA,
            &parsed) ||
        ParseSignedRange(
            L"-128",
            -COMOTE_MOUSE_MAX_WHEEL_DELTA,
            COMOTE_MOUSE_MAX_WHEEL_DELTA,
            &parsed) ||
        !ParseSignedRange(
            L"-127",
            -COMOTE_MOUSE_MAX_WHEEL_DELTA,
            COMOTE_MOUSE_MAX_WHEEL_DELTA,
            &parsed) ||
        parsed != -COMOTE_MOUSE_MAX_WHEEL_DELTA ||
        !ParseSignedRange(
            L"127",
            -COMOTE_MOUSE_MAX_WHEEL_DELTA,
            COMOTE_MOUSE_MAX_WHEEL_DELTA,
            &parsed) ||
        parsed != COMOTE_MOUSE_MAX_WHEEL_DELTA ||
        ParseSignedRange(
            L"128",
            -COMOTE_MOUSE_MAX_WHEEL_DELTA,
            COMOTE_MOUSE_MAX_WHEEL_DELTA,
            &parsed) ||
        ParseSignedRange(
            L"-1",
            0,
            COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
            &parsed) ||
        !ParseSignedRange(
            L"0",
            0,
            COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
            &parsed) ||
        parsed != 0 ||
        !ParseSignedRange(
            L"32767",
            0,
            COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
            &parsed) ||
        parsed != COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE ||
        ParseSignedRange(
            L"32768",
            0,
            COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
            &parsed))
    {
        fwprintf(
            stderr,
            L"Signed input boundary self-test failed.\n");
        return FALSE;
    }

    wprintf(
        L"Signed input boundary self-test passed.\n");
    return TRUE;
}

static
BOOL
RunInfo(
    _In_ HANDLE DeviceHandle)
{
    COMOTE_VERSION_RESPONSE version;
    COMOTE_CAPABILITIES_RESPONSE capabilities;
    DWORD bytesReturned;

    ZeroMemory(
        &version,
        sizeof(version));
    if (!SendIoctl(
            DeviceHandle,
            IOCTL_COMOTE_GET_VERSION,
            NULL,
            0,
            &version,
            (DWORD)sizeof(version),
            &bytesReturned) ||
        bytesReturned != (DWORD)sizeof(version) ||
        version.Size != (ULONG)sizeof(version) ||
        version.ProtocolVersion != COMOTE_PROTOCOL_VERSION)
    {
        fwprintf(
            stderr,
            L"Invalid version response.\n");
        return FALSE;
    }

    ZeroMemory(
        &capabilities,
        sizeof(capabilities));
    if (!SendIoctl(
            DeviceHandle,
            IOCTL_COMOTE_GET_CAPABILITIES,
            NULL,
            0,
            &capabilities,
            (DWORD)sizeof(capabilities),
            &bytesReturned) ||
        bytesReturned != (DWORD)sizeof(capabilities) ||
        capabilities.Size != (ULONG)sizeof(capabilities) ||
        capabilities.ProtocolVersion != COMOTE_PROTOCOL_VERSION)
    {
        fwprintf(
            stderr,
            L"Invalid capabilities response.\n");
        return FALSE;
    }

    wprintf(
        L"Protocol=%lu Driver=%lu.%lu.%lu.%lu Capabilities=0x%08lX\n",
        version.ProtocolVersion,
        version.DriverVersionMajor,
        version.DriverVersionMinor,
        version.DriverVersionPatch,
        version.DriverVersionBuild,
        capabilities.Capabilities);
    wprintf(
        L"KeyboardKeys=%lu MouseButtons=%lu MouseDelta=%ld "
        L"WheelDelta=%ld AbsoluteCoordinate=%lu\n",
        capabilities.MaximumKeyboardKeys,
        capabilities.MaximumMouseButtons,
        capabilities.MaximumMouseDelta,
        capabilities.MaximumWheelDelta,
        capabilities.MaximumAbsoluteCoordinate);
    return TRUE;
}

static
BOOL
RunRelease(
    _In_ HANDLE DeviceHandle,
    _In_ ULONG Sequence)
{
    COMOTE_RELEASE_ALL_REQUEST request;

    ZeroMemory(
        &request,
        sizeof(request));
    InitializeHeader(
        &request.Header,
        (ULONG)sizeof(request),
        Sequence);
    return SendIoctl(
        DeviceHandle,
        IOCTL_COMOTE_RELEASE_ALL,
        &request,
        (DWORD)sizeof(request),
        NULL,
        0,
        NULL);
}

static
BOOL
RunTap(
    _In_ HANDLE DeviceHandle,
    _In_ UCHAR Usage,
    _In_ UCHAR Modifiers)
{
    COMOTE_KEYBOARD_STATE_REQUEST request;

    if (Usage < 0x04 || Usage > 0xDF)
    {
        fwprintf(
            stderr,
            L"Keyboard usage must be between 0x04 and 0xDF.\n");
        return FALSE;
    }

    ZeroMemory(
        &request,
        sizeof(request));
    InitializeHeader(
        &request.Header,
        (ULONG)sizeof(request),
        1);
    request.Modifiers = Modifiers;
    request.Keys[0] = Usage;
    if (!SendIoctl(
            DeviceHandle,
            IOCTL_COMOTE_SET_KEYBOARD_STATE,
            &request,
            (DWORD)sizeof(request),
            NULL,
            0,
            NULL))
    {
        return FALSE;
    }

    Sleep(75);
    return RunRelease(
        DeviceHandle,
        2);
}

static
BOOL
RunMouse(
    _In_ HANDLE DeviceHandle,
    _In_ UCHAR Buttons,
    _In_ SHORT DeltaX,
    _In_ SHORT DeltaY,
    _In_ CHAR VerticalWheel,
    _In_ CHAR HorizontalWheel,
    _In_ BOOL ReleaseButtons)
{
    COMOTE_MOUSE_RELATIVE_REQUEST request;

    ZeroMemory(
        &request,
        sizeof(request));
    InitializeHeader(
        &request.Header,
        (ULONG)sizeof(request),
        1);
    request.Buttons = Buttons;
    request.DeltaX = DeltaX;
    request.DeltaY = DeltaY;
    request.VerticalWheel = VerticalWheel;
    request.HorizontalWheel = HorizontalWheel;
    if (!SendIoctl(
            DeviceHandle,
            IOCTL_COMOTE_MOUSE_RELATIVE,
            &request,
            (DWORD)sizeof(request),
            NULL,
            0,
            NULL))
    {
        return FALSE;
    }

    if (!ReleaseButtons)
    {
        return TRUE;
    }

    Sleep(50);
    ZeroMemory(
        &request,
        sizeof(request));
    InitializeHeader(
        &request.Header,
        (ULONG)sizeof(request),
        2);
    return SendIoctl(
        DeviceHandle,
        IOCTL_COMOTE_MOUSE_RELATIVE,
        &request,
        (DWORD)sizeof(request),
        NULL,
        0,
        NULL);
}

static
BOOL
RunAbsoluteMouse(
    _In_ HANDLE DeviceHandle,
    _In_ UCHAR Buttons,
    _In_ USHORT X,
    _In_ USHORT Y,
    _In_ CHAR VerticalWheel,
    _In_ CHAR HorizontalWheel,
    _In_ BOOL ReleaseButtons)
{
    COMOTE_MOUSE_ABSOLUTE_REQUEST request;

    ZeroMemory(
        &request,
        sizeof(request));
    InitializeHeader(
        &request.Header,
        (ULONG)sizeof(request),
        1);
    request.Buttons = Buttons;
    request.X = X;
    request.Y = Y;
    request.VerticalWheel = VerticalWheel;
    request.HorizontalWheel = HorizontalWheel;
    if (!SendIoctl(
            DeviceHandle,
            IOCTL_COMOTE_MOUSE_ABSOLUTE,
            &request,
            (DWORD)sizeof(request),
            NULL,
            0,
            NULL))
    {
        return FALSE;
    }

    if (!ReleaseButtons)
    {
        return TRUE;
    }

    Sleep(50);
    ZeroMemory(
        &request,
        sizeof(request));
    InitializeHeader(
        &request.Header,
        (ULONG)sizeof(request),
        2);
    request.X = X;
    request.Y = Y;
    return SendIoctl(
        DeviceHandle,
        IOCTL_COMOTE_MOUSE_ABSOLUTE,
        &request,
        (DWORD)sizeof(request),
        NULL,
        0,
        NULL);
}

static
void
PrintUsage(void)
{
    fwprintf(
        stderr,
        L"Usage:\n"
        L"  ComoteVirtualHidProbe.exe parser-selftest\n"
        L"  ComoteVirtualHidProbe.exe info\n"
        L"  ComoteVirtualHidProbe.exe release\n"
        L"  ComoteVirtualHidProbe.exe tap <usage> [modifiers]\n"
        L"  ComoteVirtualHidProbe.exe move <dx> <dy>\n"
        L"  ComoteVirtualHidProbe.exe click <button-mask>\n"
        L"  ComoteVirtualHidProbe.exe wheel <vertical> [horizontal]\n"
        L"  ComoteVirtualHidProbe.exe absolute <x> <y>\n"
        L"  ComoteVirtualHidProbe.exe absolute-click <x> <y> <button-mask>\n"
        L"  ComoteVirtualHidProbe.exe absolute-wheel <x> <y> "
        L"<vertical> [horizontal]\n");
}

int
wmain(
    int ArgumentCount,
    wchar_t** Arguments)
{
    HANDLE deviceHandle;
    BOOL result;

    if (ArgumentCount < 2)
    {
        PrintUsage();
        return 2;
    }

    if (_wcsicmp(Arguments[1], L"parser-selftest") == 0 &&
        ArgumentCount == 2)
    {
        if (!RunParserSelfTest())
        {
            return 4;
        }

        wprintf(
            L"Command completed successfully.\n");
        return 0;
    }

    deviceHandle = OpenComoteDevice();
    if (deviceHandle == INVALID_HANDLE_VALUE)
    {
        return 3;
    }

    result = FALSE;
    if (_wcsicmp(Arguments[1], L"info") == 0 &&
        ArgumentCount == 2)
    {
        result = RunInfo(deviceHandle);
    }
    else if (_wcsicmp(Arguments[1], L"release") == 0 &&
        ArgumentCount == 2)
    {
        result = RunRelease(
            deviceHandle,
            1);
    }
    else if (_wcsicmp(Arguments[1], L"tap") == 0 &&
        (ArgumentCount == 3 || ArgumentCount == 4))
    {
        UCHAR usage;
        UCHAR modifiers;

        modifiers = 0;
        if (ParseUnsignedByte(Arguments[2], &usage) &&
            (ArgumentCount == 3 ||
                ParseUnsignedByte(Arguments[3], &modifiers)))
        {
            result = RunTap(
                deviceHandle,
                usage,
                modifiers);
        }
    }
    else if (_wcsicmp(Arguments[1], L"move") == 0 &&
        ArgumentCount == 4)
    {
        long deltaX;
        long deltaY;

        if (ParseSignedRange(
                Arguments[2],
                -COMOTE_MOUSE_MAX_DELTA,
                COMOTE_MOUSE_MAX_DELTA,
                &deltaX) &&
            ParseSignedRange(
                Arguments[3],
                -COMOTE_MOUSE_MAX_DELTA,
                COMOTE_MOUSE_MAX_DELTA,
                &deltaY))
        {
            result = RunMouse(
                deviceHandle,
                0,
                (SHORT)deltaX,
                (SHORT)deltaY,
                0,
                0,
                FALSE);
        }
    }
    else if (_wcsicmp(Arguments[1], L"click") == 0 &&
        ArgumentCount == 3)
    {
        UCHAR buttons;

        if (ParseUnsignedByte(Arguments[2], &buttons) &&
            buttons != 0 &&
            (buttons & ~COMOTE_MOUSE_BUTTON_MASK) == 0)
        {
            result = RunMouse(
                deviceHandle,
                buttons,
                0,
                0,
                0,
                0,
                TRUE);
        }
    }
    else if (_wcsicmp(Arguments[1], L"wheel") == 0 &&
        (ArgumentCount == 3 || ArgumentCount == 4))
    {
        long vertical;
        long horizontal;

        horizontal = 0;
        if (ParseSignedRange(
                Arguments[2],
                -COMOTE_MOUSE_MAX_WHEEL_DELTA,
                COMOTE_MOUSE_MAX_WHEEL_DELTA,
                &vertical) &&
            (ArgumentCount == 3 ||
                ParseSignedRange(
                    Arguments[3],
                    -COMOTE_MOUSE_MAX_WHEEL_DELTA,
                    COMOTE_MOUSE_MAX_WHEEL_DELTA,
                    &horizontal)))
        {
            result = RunMouse(
                deviceHandle,
                0,
                0,
                0,
                (CHAR)vertical,
                (CHAR)horizontal,
                FALSE);
        }
    }
    else if (_wcsicmp(Arguments[1], L"absolute") == 0 &&
        ArgumentCount == 4)
    {
        long x;
        long y;

        if (ParseSignedRange(
                Arguments[2],
                0,
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
                &x) &&
            ParseSignedRange(
                Arguments[3],
                0,
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
                &y))
        {
            result = RunAbsoluteMouse(
                deviceHandle,
                0,
                (USHORT)x,
                (USHORT)y,
                0,
                0,
                FALSE);
        }
    }
    else if (_wcsicmp(Arguments[1], L"absolute-click") == 0 &&
        ArgumentCount == 5)
    {
        long x;
        long y;
        UCHAR buttons;

        if (ParseSignedRange(
                Arguments[2],
                0,
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
                &x) &&
            ParseSignedRange(
                Arguments[3],
                0,
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
                &y) &&
            ParseUnsignedByte(Arguments[4], &buttons) &&
            buttons != 0 &&
            (buttons & ~COMOTE_MOUSE_BUTTON_MASK) == 0)
        {
            result = RunAbsoluteMouse(
                deviceHandle,
                buttons,
                (USHORT)x,
                (USHORT)y,
                0,
                0,
                TRUE);
        }
    }
    else if (_wcsicmp(Arguments[1], L"absolute-wheel") == 0 &&
        (ArgumentCount == 5 || ArgumentCount == 6))
    {
        long x;
        long y;
        long vertical;
        long horizontal;

        horizontal = 0;
        if (ParseSignedRange(
                Arguments[2],
                0,
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
                &x) &&
            ParseSignedRange(
                Arguments[3],
                0,
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE,
                &y) &&
            ParseSignedRange(
                Arguments[4],
                -COMOTE_MOUSE_MAX_WHEEL_DELTA,
                COMOTE_MOUSE_MAX_WHEEL_DELTA,
                &vertical) &&
            (ArgumentCount == 5 ||
                ParseSignedRange(
                    Arguments[5],
                    -COMOTE_MOUSE_MAX_WHEEL_DELTA,
                    COMOTE_MOUSE_MAX_WHEEL_DELTA,
                    &horizontal)))
        {
            result = RunAbsoluteMouse(
                deviceHandle,
                0,
                (USHORT)x,
                (USHORT)y,
                (CHAR)vertical,
                (CHAR)horizontal,
                FALSE);
        }
    }
    else
    {
        PrintUsage();
    }

    CloseHandle(deviceHandle);
    if (!result)
    {
        fwprintf(
            stderr,
            L"Command failed or its arguments were invalid.\n");
        return 4;
    }

    wprintf(
        L"Command completed successfully.\n");
    return 0;
}
