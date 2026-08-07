#include "ComoteVirtualHidPhase2.h"

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, ComoteEvtDeviceAdd)
#pragma alloc_text(PAGE, ComoteEvtDeviceCleanup)
#pragma alloc_text(PAGE, ComoteEvtDeviceFileCreate)
#pragma alloc_text(PAGE, ComoteEvtFileCleanup)
#pragma alloc_text(PAGE, ComoteEvtIoDeviceControl)
#pragma alloc_text(PAGE, ComoteEvtDeviceD0Entry)
#pragma alloc_text(PAGE, ComoteEvtDeviceD0Exit)
#endif

static UCHAR ComoteReportDescriptor[] =
{
    0x05, 0x01,
    0x09, 0x06,
    0xA1, 0x01,
    0x85, COMOTE_KEYBOARD_REPORT_ID,
    0x05, 0x07,
    0x19, 0xE0,
    0x29, 0xE7,
    0x15, 0x00,
    0x25, 0x01,
    0x75, 0x01,
    0x95, 0x08,
    0x81, 0x02,
    0x75, 0x08,
    0x95, 0x01,
    0x81, 0x03,
    0x19, 0x00,
    0x29, 0xFF,
    0x15, 0x00,
    0x26, 0xFF, 0x00,
    0x75, 0x08,
    0x95, 0x06,
    0x81, 0x00,
    0xC0,

    0x05, 0x01,
    0x09, 0x02,
    0xA1, 0x01,
    0x85, COMOTE_MOUSE_REPORT_ID,
    0x09, 0x01,
    0xA1, 0x00,
    0x05, 0x09,
    0x19, 0x01,
    0x29, 0x05,
    0x15, 0x00,
    0x25, 0x01,
    0x75, 0x01,
    0x95, 0x05,
    0x81, 0x02,
    0x75, 0x03,
    0x95, 0x01,
    0x81, 0x03,
    0x05, 0x01,
    0x09, 0x30,
    0x09, 0x31,
    0x16, 0x01, 0x80,
    0x26, 0xFF, 0x7F,
    0x75, 0x10,
    0x95, 0x02,
    0x81, 0x06,
    0x09, 0x38,
    0x15, 0x81,
    0x25, 0x7F,
    0x75, 0x08,
    0x95, 0x01,
    0x81, 0x06,
    0x05, 0x0C,
    0x0A, 0x38, 0x02,
    0x15, 0x81,
    0x25, 0x7F,
    0x75, 0x08,
    0x95, 0x01,
    0x81, 0x06,
    0xC0,
    0xC0,

    0x05, 0x01,
    0x09, 0x02,
    0xA1, 0x01,
    0x85, COMOTE_ABSOLUTE_MOUSE_REPORT_ID,
    0x09, 0x01,
    0xA1, 0x00,
    0x05, 0x09,
    0x19, 0x01,
    0x29, 0x05,
    0x15, 0x00,
    0x25, 0x01,
    0x75, 0x01,
    0x95, 0x05,
    0x81, 0x02,
    0x75, 0x03,
    0x95, 0x01,
    0x81, 0x03,
    0x05, 0x01,
    0x09, 0x30,
    0x09, 0x31,
    0x15, 0x00,
    0x26, 0xFF, 0x7F,
    0x75, 0x10,
    0x95, 0x02,
    0x81, 0x02,
    0x09, 0x38,
    0x15, 0x81,
    0x25, 0x7F,
    0x75, 0x08,
    0x95, 0x01,
    0x81, 0x06,
    0x05, 0x0C,
    0x0A, 0x38, 0x02,
    0x15, 0x81,
    0x25, 0x7F,
    0x75, 0x08,
    0x95, 0x01,
    0x81, 0x06,
    0xC0,
    0xC0
};

static
NTSTATUS
ComoteSubmitKeyboardReport(
    _In_ PCOMOTE_DEVICE_CONTEXT DeviceContext,
    _In_ PCOMOTE_KEYBOARD_REPORT Report)
{
    HID_XFER_PACKET packet;

    if (DeviceContext->VhfHandle == NULL)
    {
        return STATUS_DEVICE_NOT_READY;
    }

    RtlZeroMemory(
        &packet,
        sizeof(packet));
    packet.reportBuffer = (PUCHAR)Report;
    packet.reportBufferLen = (ULONG)sizeof(*Report);
    packet.reportId = COMOTE_KEYBOARD_REPORT_ID;

    return VhfReadReportSubmit(
        DeviceContext->VhfHandle,
        &packet);
}

static
NTSTATUS
ComoteSubmitMouseReport(
    _In_ PCOMOTE_DEVICE_CONTEXT DeviceContext,
    _In_ PCOMOTE_MOUSE_REPORT Report)
{
    HID_XFER_PACKET packet;

    if (DeviceContext->VhfHandle == NULL)
    {
        return STATUS_DEVICE_NOT_READY;
    }

    RtlZeroMemory(
        &packet,
        sizeof(packet));
    packet.reportBuffer = (PUCHAR)Report;
    packet.reportBufferLen = (ULONG)sizeof(*Report);
    packet.reportId = COMOTE_MOUSE_REPORT_ID;

    return VhfReadReportSubmit(
        DeviceContext->VhfHandle,
        &packet);
}

static
NTSTATUS
ComoteSubmitAbsoluteMouseReport(
    _In_ PCOMOTE_DEVICE_CONTEXT DeviceContext,
    _In_ PCOMOTE_ABSOLUTE_MOUSE_REPORT Report)
{
    HID_XFER_PACKET packet;

    if (DeviceContext->VhfHandle == NULL)
    {
        return STATUS_DEVICE_NOT_READY;
    }

    RtlZeroMemory(
        &packet,
        sizeof(packet));
    packet.reportBuffer = (PUCHAR)Report;
    packet.reportBufferLen = (ULONG)sizeof(*Report);
    packet.reportId = COMOTE_ABSOLUTE_MOUSE_REPORT_ID;

    return VhfReadReportSubmit(
        DeviceContext->VhfHandle,
        &packet);
}

static
NTSTATUS
ComoteReleaseAll(
    _In_ PCOMOTE_DEVICE_CONTEXT DeviceContext)
{
    COMOTE_KEYBOARD_REPORT keyboardReport;
    COMOTE_MOUSE_REPORT mouseReport;
    COMOTE_ABSOLUTE_MOUSE_REPORT absoluteMouseReport;
    NTSTATUS keyboardStatus;
    NTSTATUS mouseStatus;
    NTSTATUS absoluteMouseStatus;

    DeviceContext->ReleasePending = TRUE;

    RtlZeroMemory(
        &keyboardReport,
        sizeof(keyboardReport));
    keyboardReport.ReportId = COMOTE_KEYBOARD_REPORT_ID;

    RtlZeroMemory(
        &mouseReport,
        sizeof(mouseReport));
    mouseReport.ReportId = COMOTE_MOUSE_REPORT_ID;

    RtlZeroMemory(
        &absoluteMouseReport,
        sizeof(absoluteMouseReport));
    absoluteMouseReport.ReportId =
        COMOTE_ABSOLUTE_MOUSE_REPORT_ID;
    absoluteMouseReport.X = DeviceContext->AbsoluteMouseX;
    absoluteMouseReport.Y = DeviceContext->AbsoluteMouseY;

    keyboardStatus = ComoteSubmitKeyboardReport(
        DeviceContext,
        &keyboardReport);
    if (NT_SUCCESS(keyboardStatus))
    {
        DeviceContext->KeyboardState = keyboardReport;
    }

    mouseStatus = ComoteSubmitMouseReport(
        DeviceContext,
        &mouseReport);
    if (NT_SUCCESS(mouseStatus))
    {
        DeviceContext->RelativeMouseButtons = 0;
    }

    absoluteMouseStatus = STATUS_SUCCESS;
    if (DeviceContext->AbsoluteMouseInitialized)
    {
        absoluteMouseStatus = ComoteSubmitAbsoluteMouseReport(
            DeviceContext,
            &absoluteMouseReport);
        if (NT_SUCCESS(absoluteMouseStatus))
        {
            DeviceContext->AbsoluteMouseButtons = 0;
        }
    }

    if (!NT_SUCCESS(keyboardStatus))
    {
        return keyboardStatus;
    }

    if (!NT_SUCCESS(mouseStatus))
    {
        return mouseStatus;
    }

    if (!NT_SUCCESS(absoluteMouseStatus))
    {
        return absoluteMouseStatus;
    }

    DeviceContext->ReleasePending = FALSE;
    return STATUS_SUCCESS;
}

static
NTSTATUS
ComoteValidateCommandHeader(
    _In_ PCOMOTE_DEVICE_CONTEXT DeviceContext,
    _In_ const COMOTE_COMMAND_HEADER* Header,
    _In_ ULONG ExpectedSize)
{
    if (Header->Size != ExpectedSize ||
        Header->Magic != COMOTE_PROTOCOL_MAGIC ||
        Header->ProtocolVersion != COMOTE_PROTOCOL_VERSION)
    {
        return STATUS_INVALID_PARAMETER;
    }

    if (DeviceContext->LastSequence == MAXULONG ||
        Header->Sequence != DeviceContext->LastSequence + 1)
    {
        return STATUS_INVALID_PARAMETER;
    }

    return STATUS_SUCCESS;
}

static
NTSTATUS
ComoteValidateKeyboardRequest(
    _In_ const COMOTE_KEYBOARD_STATE_REQUEST* Request)
{
    ULONG index;
    ULONG otherIndex;

    if (Request->Reserved != 0)
    {
        return STATUS_INVALID_PARAMETER;
    }

    for (index = 0; index < COMOTE_KEYBOARD_MAX_KEYS; index++)
    {
        UCHAR usage = Request->Keys[index];

        if (usage == 0)
        {
            continue;
        }
        if (usage < 0x04 || usage > 0xDF)
        {
            return STATUS_INVALID_PARAMETER;
        }
        for (otherIndex = index + 1;
             otherIndex < COMOTE_KEYBOARD_MAX_KEYS;
             otherIndex++)
        {
            if (Request->Keys[otherIndex] == usage)
            {
                return STATUS_INVALID_PARAMETER;
            }
        }
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS
DriverEntry(
    PDRIVER_OBJECT DriverObject,
    PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG driverConfig;

    WDF_DRIVER_CONFIG_INIT(
        &driverConfig,
        ComoteEvtDeviceAdd);

    return WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &driverConfig,
        WDF_NO_HANDLE);
}

_Use_decl_annotations_
NTSTATUS
ComoteEvtDeviceAdd(
    WDFDRIVER Driver,
    PWDFDEVICE_INIT DeviceInit)
{
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_FILEOBJECT_CONFIG fileObjectConfig;
    WDF_PNPPOWER_EVENT_CALLBACKS powerCallbacks;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDFDEVICE device;
    PCOMOTE_DEVICE_CONTEXT deviceContext;
    VHF_CONFIG vhfConfig;
    UNICODE_STRING securityDescriptor;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Driver);
    PAGED_CODE();

    WdfDeviceInitSetDeviceType(
        DeviceInit,
        FILE_DEVICE_COMOTE_VIRTUAL_HID);
    WdfDeviceInitSetCharacteristics(
        DeviceInit,
        FILE_DEVICE_SECURE_OPEN |
            FILE_AUTOGENERATED_DEVICE_NAME,
        FALSE);
    WdfDeviceInitSetExclusive(
        DeviceInit,
        TRUE);

    securityDescriptor = SDDL_DEVOBJ_SYS_ALL;
    status = WdfDeviceInitAssignSDDLString(
        DeviceInit,
        &securityDescriptor);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_FILEOBJECT_CONFIG_INIT(
        &fileObjectConfig,
        ComoteEvtDeviceFileCreate,
        WDF_NO_EVENT_CALLBACK,
        ComoteEvtFileCleanup);
    WdfDeviceInitSetFileObjectConfig(
        DeviceInit,
        &fileObjectConfig,
        WDF_NO_OBJECT_ATTRIBUTES);

    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(
        &powerCallbacks);
    powerCallbacks.EvtDeviceD0Entry = ComoteEvtDeviceD0Entry;
    powerCallbacks.EvtDeviceD0Exit = ComoteEvtDeviceD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(
        DeviceInit,
        &powerCallbacks);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &deviceAttributes,
        COMOTE_DEVICE_CONTEXT);
    deviceAttributes.EvtCleanupCallback = ComoteEvtDeviceCleanup;
    deviceAttributes.ExecutionLevel = WdfExecutionLevelPassive;
    deviceAttributes.SynchronizationScope =
        WdfSynchronizationScopeDevice;

    status = WdfDeviceCreate(
        &DeviceInit,
        &deviceAttributes,
        &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    deviceContext = ComoteGetDeviceContext(device);
    RtlZeroMemory(
        deviceContext,
        sizeof(*deviceContext));
    deviceContext->KeyboardState.ReportId =
        COMOTE_KEYBOARD_REPORT_ID;

    status = WdfDeviceCreateDeviceInterface(
        device,
        &GUID_DEVINTERFACE_COMOTE_VIRTUAL_HID_PHASE2,
        NULL);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig,
        WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl =
        ComoteEvtIoDeviceControl;
    status = WdfIoQueueCreate(
        device,
        &queueConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        WDF_NO_HANDLE);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    VHF_CONFIG_INIT(
        &vhfConfig,
        WdfDeviceWdmGetDeviceObject(device),
        (USHORT)sizeof(ComoteReportDescriptor),
        ComoteReportDescriptor);
    vhfConfig.VendorID = 0;
    vhfConfig.ProductID = 0;
    vhfConfig.VersionNumber = 2;

    status = VhfCreate(
        &vhfConfig,
        &deviceContext->VhfHandle);
    if (!NT_SUCCESS(status))
    {
        deviceContext->VhfHandle = NULL;
        return status;
    }

    status = VhfStart(
        deviceContext->VhfHandle);
    if (!NT_SUCCESS(status))
    {
        VhfDelete(
            deviceContext->VhfHandle,
            TRUE);
        deviceContext->VhfHandle = NULL;
        return status;
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID
ComoteEvtDeviceFileCreate(
    WDFDEVICE Device,
    WDFREQUEST Request,
    WDFFILEOBJECT FileObject)
{
    PCOMOTE_DEVICE_CONTEXT deviceContext;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(FileObject);
    PAGED_CODE();

    deviceContext = ComoteGetDeviceContext(Device);
    status = ComoteReleaseAll(deviceContext);
    if (NT_SUCCESS(status))
    {
        deviceContext->LastSequence = 0;
    }

    WdfRequestComplete(
        Request,
        status);
}

_Use_decl_annotations_
VOID
ComoteEvtFileCleanup(
    WDFFILEOBJECT FileObject)
{
    WDFDEVICE device;
    PCOMOTE_DEVICE_CONTEXT deviceContext;

    PAGED_CODE();

    device = WdfFileObjectGetDevice(FileObject);
    deviceContext = ComoteGetDeviceContext(device);
    (VOID)ComoteReleaseAll(deviceContext);
    deviceContext->LastSequence = 0;
}

_Use_decl_annotations_
VOID
ComoteEvtIoDeviceControl(
    WDFQUEUE Queue,
    WDFREQUEST Request,
    size_t OutputBufferLength,
    size_t InputBufferLength,
    ULONG IoControlCode)
{
    WDFDEVICE device;
    PCOMOTE_DEVICE_CONTEXT deviceContext;
    NTSTATUS status;
    size_t information;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    PAGED_CODE();

    device = WdfIoQueueGetDevice(Queue);
    deviceContext = ComoteGetDeviceContext(device);
    status = STATUS_INVALID_DEVICE_REQUEST;
    information = 0;

    switch (IoControlCode)
    {
    case IOCTL_COMOTE_GET_VERSION:
    {
        PCOMOTE_VERSION_RESPONSE response;

        if (InputBufferLength != 0)
        {
            status = STATUS_INVALID_BUFFER_SIZE;
            break;
        }
        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(*response),
            (PVOID*)&response,
            NULL);
        if (NT_SUCCESS(status))
        {
            RtlZeroMemory(
                response,
                sizeof(*response));
            response->Size = (ULONG)sizeof(*response);
            response->ProtocolVersion = COMOTE_PROTOCOL_VERSION;
            response->DriverVersionMajor =
                COMOTE_DRIVER_VERSION_MAJOR;
            response->DriverVersionMinor =
                COMOTE_DRIVER_VERSION_MINOR;
            response->DriverVersionPatch =
                COMOTE_DRIVER_VERSION_PATCH;
            response->DriverVersionBuild =
                COMOTE_DRIVER_VERSION_BUILD;
            information = sizeof(*response);
        }
        break;
    }

    case IOCTL_COMOTE_GET_CAPABILITIES:
    {
        PCOMOTE_CAPABILITIES_RESPONSE response;

        if (InputBufferLength != 0)
        {
            status = STATUS_INVALID_BUFFER_SIZE;
            break;
        }
        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(*response),
            (PVOID*)&response,
            NULL);
        if (NT_SUCCESS(status))
        {
            RtlZeroMemory(
                response,
                sizeof(*response));
            response->Size = (ULONG)sizeof(*response);
            response->ProtocolVersion = COMOTE_PROTOCOL_VERSION;
            response->Capabilities =
                COMOTE_CAPABILITY_KEYBOARD_6KRO |
                COMOTE_CAPABILITY_MOUSE_RELATIVE |
                COMOTE_CAPABILITY_MOUSE_ABSOLUTE |
                COMOTE_CAPABILITY_MOUSE_FIVE_BUTTONS |
                COMOTE_CAPABILITY_MOUSE_VERTICAL_WHEEL |
                COMOTE_CAPABILITY_MOUSE_HORIZONTAL_WHEEL |
                COMOTE_CAPABILITY_RELEASE_ALL |
                COMOTE_CAPABILITY_STRICT_SEQUENCE |
                COMOTE_CAPABILITY_SYSTEM_ONLY |
                COMOTE_CAPABILITY_RELEASE_ON_CLOSE |
                COMOTE_CAPABILITY_RELEASE_ON_POWER_DOWN;
            response->MaximumKeyboardKeys =
                COMOTE_KEYBOARD_MAX_KEYS;
            response->MaximumMouseButtons = 5;
            response->MaximumMouseDelta =
                COMOTE_MOUSE_MAX_DELTA;
            response->MaximumWheelDelta =
                COMOTE_MOUSE_MAX_WHEEL_DELTA;
            response->MaximumAbsoluteCoordinate =
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE;
            information = sizeof(*response);
        }
        break;
    }

    case IOCTL_COMOTE_RELEASE_ALL:
    {
        PCOMOTE_RELEASE_ALL_REQUEST releaseRequest;

        if (InputBufferLength != sizeof(*releaseRequest))
        {
            status = STATUS_INVALID_BUFFER_SIZE;
            break;
        }
        status = WdfRequestRetrieveInputBuffer(
            Request,
            sizeof(*releaseRequest),
            (PVOID*)&releaseRequest,
            NULL);
        if (!NT_SUCCESS(status))
        {
            break;
        }
        status = ComoteValidateCommandHeader(
            deviceContext,
            &releaseRequest->Header,
            (ULONG)sizeof(*releaseRequest));
        if (!NT_SUCCESS(status))
        {
            break;
        }

        status = ComoteReleaseAll(deviceContext);
        if (NT_SUCCESS(status))
        {
            deviceContext->LastSequence =
                releaseRequest->Header.Sequence;
        }
        break;
    }

    case IOCTL_COMOTE_SET_KEYBOARD_STATE:
    {
        PCOMOTE_KEYBOARD_STATE_REQUEST keyboardRequest;
        COMOTE_KEYBOARD_REPORT keyboardReport;

        if (InputBufferLength != sizeof(*keyboardRequest))
        {
            status = STATUS_INVALID_BUFFER_SIZE;
            break;
        }
        status = WdfRequestRetrieveInputBuffer(
            Request,
            sizeof(*keyboardRequest),
            (PVOID*)&keyboardRequest,
            NULL);
        if (!NT_SUCCESS(status))
        {
            break;
        }
        status = ComoteValidateCommandHeader(
            deviceContext,
            &keyboardRequest->Header,
            (ULONG)sizeof(*keyboardRequest));
        if (!NT_SUCCESS(status))
        {
            break;
        }
        status = ComoteValidateKeyboardRequest(
            keyboardRequest);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        RtlZeroMemory(
            &keyboardReport,
            sizeof(keyboardReport));
        keyboardReport.ReportId =
            COMOTE_KEYBOARD_REPORT_ID;
        keyboardReport.Modifiers =
            keyboardRequest->Modifiers;
        RtlCopyMemory(
            keyboardReport.Keys,
            keyboardRequest->Keys,
            sizeof(keyboardReport.Keys));

        status = ComoteSubmitKeyboardReport(
            deviceContext,
            &keyboardReport);
        if (NT_SUCCESS(status))
        {
            deviceContext->KeyboardState =
                keyboardReport;
            deviceContext->LastSequence =
                keyboardRequest->Header.Sequence;
        }
        break;
    }

    case IOCTL_COMOTE_MOUSE_RELATIVE:
    {
        PCOMOTE_MOUSE_RELATIVE_REQUEST mouseRequest;
        COMOTE_MOUSE_REPORT mouseReport;

        if (InputBufferLength != sizeof(*mouseRequest))
        {
            status = STATUS_INVALID_BUFFER_SIZE;
            break;
        }
        status = WdfRequestRetrieveInputBuffer(
            Request,
            sizeof(*mouseRequest),
            (PVOID*)&mouseRequest,
            NULL);
        if (!NT_SUCCESS(status))
        {
            break;
        }
        status = ComoteValidateCommandHeader(
            deviceContext,
            &mouseRequest->Header,
            (ULONG)sizeof(*mouseRequest));
        if (!NT_SUCCESS(status))
        {
            break;
        }
        if (mouseRequest->Reserved != 0 ||
            (mouseRequest->Buttons &
                ~COMOTE_MOUSE_BUTTON_MASK) != 0 ||
            mouseRequest->DeltaX <
                -COMOTE_MOUSE_MAX_DELTA ||
            mouseRequest->DeltaY <
                -COMOTE_MOUSE_MAX_DELTA ||
            mouseRequest->VerticalWheel <
                -COMOTE_MOUSE_MAX_WHEEL_DELTA ||
            mouseRequest->HorizontalWheel <
                -COMOTE_MOUSE_MAX_WHEEL_DELTA)
        {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        RtlZeroMemory(
            &mouseReport,
            sizeof(mouseReport));
        mouseReport.ReportId = COMOTE_MOUSE_REPORT_ID;
        mouseReport.Buttons = mouseRequest->Buttons;
        mouseReport.DeltaX = mouseRequest->DeltaX;
        mouseReport.DeltaY = mouseRequest->DeltaY;
        mouseReport.VerticalWheel =
            mouseRequest->VerticalWheel;
        mouseReport.HorizontalWheel =
            mouseRequest->HorizontalWheel;

        status = ComoteSubmitMouseReport(
            deviceContext,
            &mouseReport);
        if (NT_SUCCESS(status))
        {
            deviceContext->RelativeMouseButtons =
                mouseReport.Buttons;
            deviceContext->LastSequence =
                mouseRequest->Header.Sequence;
        }
        break;
    }

    case IOCTL_COMOTE_MOUSE_ABSOLUTE:
    {
        PCOMOTE_MOUSE_ABSOLUTE_REQUEST mouseRequest;
        COMOTE_ABSOLUTE_MOUSE_REPORT mouseReport;

        if (InputBufferLength != sizeof(*mouseRequest))
        {
            status = STATUS_INVALID_BUFFER_SIZE;
            break;
        }
        status = WdfRequestRetrieveInputBuffer(
            Request,
            sizeof(*mouseRequest),
            (PVOID*)&mouseRequest,
            NULL);
        if (!NT_SUCCESS(status))
        {
            break;
        }
        status = ComoteValidateCommandHeader(
            deviceContext,
            &mouseRequest->Header,
            (ULONG)sizeof(*mouseRequest));
        if (!NT_SUCCESS(status))
        {
            break;
        }
        if (mouseRequest->Reserved != 0 ||
            (mouseRequest->Buttons &
                ~COMOTE_MOUSE_BUTTON_MASK) != 0 ||
            mouseRequest->X >
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE ||
            mouseRequest->Y >
                COMOTE_MOUSE_MAX_ABSOLUTE_COORDINATE ||
            mouseRequest->VerticalWheel <
                -COMOTE_MOUSE_MAX_WHEEL_DELTA ||
            mouseRequest->HorizontalWheel <
                -COMOTE_MOUSE_MAX_WHEEL_DELTA)
        {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        RtlZeroMemory(
            &mouseReport,
            sizeof(mouseReport));
        mouseReport.ReportId =
            COMOTE_ABSOLUTE_MOUSE_REPORT_ID;
        mouseReport.Buttons = mouseRequest->Buttons;
        mouseReport.X = mouseRequest->X;
        mouseReport.Y = mouseRequest->Y;
        mouseReport.VerticalWheel =
            mouseRequest->VerticalWheel;
        mouseReport.HorizontalWheel =
            mouseRequest->HorizontalWheel;

        status = ComoteSubmitAbsoluteMouseReport(
            deviceContext,
            &mouseReport);
        if (NT_SUCCESS(status))
        {
            deviceContext->AbsoluteMouseButtons =
                mouseReport.Buttons;
            deviceContext->AbsoluteMouseX = mouseReport.X;
            deviceContext->AbsoluteMouseY = mouseReport.Y;
            deviceContext->AbsoluteMouseInitialized = TRUE;
            deviceContext->LastSequence =
                mouseRequest->Header.Sequence;
        }
        break;
    }

    default:
        break;
    }

    WdfRequestCompleteWithInformation(
        Request,
        status,
        information);
}

_Use_decl_annotations_
NTSTATUS
ComoteEvtDeviceD0Entry(
    WDFDEVICE Device,
    WDF_POWER_DEVICE_STATE PreviousState)
{
    PCOMOTE_DEVICE_CONTEXT deviceContext;

    UNREFERENCED_PARAMETER(PreviousState);
    PAGED_CODE();

    deviceContext = ComoteGetDeviceContext(Device);
    if (!deviceContext->ReleasePending)
    {
        return STATUS_SUCCESS;
    }

    return ComoteReleaseAll(deviceContext);
}

_Use_decl_annotations_
NTSTATUS
ComoteEvtDeviceD0Exit(
    WDFDEVICE Device,
    WDF_POWER_DEVICE_STATE TargetState)
{
    PCOMOTE_DEVICE_CONTEXT deviceContext;

    UNREFERENCED_PARAMETER(TargetState);
    PAGED_CODE();

    deviceContext = ComoteGetDeviceContext(Device);
    (VOID)ComoteReleaseAll(deviceContext);

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
VOID
ComoteEvtDeviceCleanup(
    WDFOBJECT DeviceObject)
{
    WDFDEVICE device;
    PCOMOTE_DEVICE_CONTEXT deviceContext;

    PAGED_CODE();

    device = (WDFDEVICE)DeviceObject;
    deviceContext = ComoteGetDeviceContext(device);

    if (deviceContext->VhfHandle != NULL)
    {
        (VOID)ComoteReleaseAll(deviceContext);
        VhfDelete(
            deviceContext->VhfHandle,
            TRUE);
        deviceContext->VhfHandle = NULL;
    }
    deviceContext->LastSequence = 0;
}
