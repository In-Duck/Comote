#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>
#include <vhf.h>

#include "public.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD ComoteVhidEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP ComoteVhidEvtDeviceCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL ComoteVhidEvtIoDeviceControl;

typedef struct _DEVICE_CONTEXT {
    VHFHANDLE VhfHandle;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, ComoteVhidGetContext);

static const UCHAR ComoteVhidReportDescriptor[] = {
    // Report 1: boot-compatible keyboard (modifier, reserved, six keys).
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x06,       // Usage (Keyboard)
    0xA1, 0x01,       // Collection (Application)
    0x85, 0x01,       //   Report ID (1)
    0x05, 0x07,       //   Usage Page (Keyboard)
    0x19, 0xE0,       //   Usage Minimum (Left Control)
    0x29, 0xE7,       //   Usage Maximum (Right GUI)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0x01,       //   Logical Maximum (1)
    0x75, 0x01,       //   Report Size (1)
    0x95, 0x08,       //   Report Count (8)
    0x81, 0x02,       //   Input (Data, Variable, Absolute)
    0x95, 0x01,       //   Report Count (1)
    0x75, 0x08,       //   Report Size (8)
    0x81, 0x01,       //   Input (Constant)
    0x95, 0x06,       //   Report Count (6)
    0x75, 0x08,       //   Report Size (8)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0x65,       //   Logical Maximum (101)
    0x05, 0x07,       //   Usage Page (Keyboard)
    0x19, 0x00,       //   Usage Minimum (Reserved)
    0x29, 0x65,       //   Usage Maximum (Keyboard Application)
    0x81, 0x00,       //   Input (Data, Array, Absolute)
    0xC0,             // End Collection

    // Report 2: five-button absolute mouse with vertical wheel.
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x02,       // Usage (Mouse)
    0xA1, 0x01,       // Collection (Application)
    0x85, 0x02,       //   Report ID (2)
    0x09, 0x01,       //   Usage (Pointer)
    0xA1, 0x00,       //   Collection (Physical)
    0x05, 0x09,       //     Usage Page (Button)
    0x19, 0x01,       //     Usage Minimum (Button 1)
    0x29, 0x05,       //     Usage Maximum (Button 5)
    0x15, 0x00,       //     Logical Minimum (0)
    0x25, 0x01,       //     Logical Maximum (1)
    0x95, 0x05,       //     Report Count (5)
    0x75, 0x01,       //     Report Size (1)
    0x81, 0x02,       //     Input (Data, Variable, Absolute)
    0x95, 0x01,       //     Report Count (1)
    0x75, 0x03,       //     Report Size (3)
    0x81, 0x01,       //     Input (Constant)
    0x05, 0x01,       //     Usage Page (Generic Desktop)
    0x09, 0x30,       //     Usage (X)
    0x09, 0x31,       //     Usage (Y)
    0x16, 0x00, 0x00, //     Logical Minimum (0)
    0x26, 0xFF, 0x7F, //     Logical Maximum (32767)
    0x75, 0x10,       //     Report Size (16)
    0x95, 0x02,       //     Report Count (2)
    0x81, 0x02,       //     Input (Data, Variable, Absolute)
    0x09, 0x38,       //     Usage (Wheel)
    0x15, 0x81,       //     Logical Minimum (-127)
    0x25, 0x7F,       //     Logical Maximum (127)
    0x75, 0x08,       //     Report Size (8)
    0x95, 0x01,       //     Report Count (1)
    0x81, 0x06,       //     Input (Data, Variable, Relative)
    0xC0,             //   End Collection
    0xC0,             // End Collection
};

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, ComoteVhidEvtDeviceAdd);
    return WdfDriverCreate(
        DriverObject,
        RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);
}

NTSTATUS
ComoteVhidEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
)
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    VHF_CONFIG vhfConfig;
    PDEVICE_CONTEXT context;

    UNREFERENCED_PARAMETER(Driver);

    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = ComoteVhidEvtDeviceCleanup;

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    context = ComoteVhidGetContext(device);
    context->VhfHandle = NULL;

    status = WdfDeviceCreateDeviceInterface(
        device,
        &GUID_DEVINTERFACE_COMOTE_VIRTUAL_HID,
        NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig,
        WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = ComoteVhidEvtIoDeviceControl;

    status = WdfIoQueueCreate(
        device,
        &queueConfig,
        WDF_NO_OBJECT_ATTRIBUTES,
        WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    VHF_CONFIG_INIT(
        &vhfConfig,
        WdfDeviceWdmGetDeviceObject(device),
        (USHORT)sizeof(ComoteVhidReportDescriptor),
        (PUCHAR)ComoteVhidReportDescriptor);
    vhfConfig.VendorID = 0xCAFE;
    vhfConfig.ProductID = 0xC054;
    vhfConfig.VersionNumber = 0x0100;

    status = VhfCreate(&vhfConfig, &context->VhfHandle);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = VhfStart(context->VhfHandle);
    if (!NT_SUCCESS(status)) {
        VhfDelete(context->VhfHandle, TRUE);
        context->VhfHandle = NULL;
    }

    return status;
}

VOID
ComoteVhidEvtDeviceCleanup(
    _In_ WDFOBJECT DeviceObject
)
{
    PDEVICE_CONTEXT context =
        ComoteVhidGetContext((WDFDEVICE)DeviceObject);

    if (context->VhfHandle != NULL) {
        VhfDelete(context->VhfHandle, TRUE);
        context->VhfHandle = NULL;
    }
}

VOID
ComoteVhidEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
)
{
    NTSTATUS status;
    WDFDEVICE device;
    PDEVICE_CONTEXT context;
    PUCHAR reportBuffer;
    size_t reportLength;
    size_t expectedLength;
    HID_XFER_PACKET packet;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    if (IoControlCode != IOCTL_COMOTE_VHID_SUBMIT_REPORT) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }

    status = WdfRequestRetrieveInputBuffer(
        Request,
        1,
        (PVOID*)&reportBuffer,
        &reportLength);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    expectedLength = reportBuffer[0] == 1 ? 9 :
        reportBuffer[0] == 2 ? 7 : 0;
    if (expectedLength == 0 || reportLength != expectedLength) {
        WdfRequestComplete(Request, STATUS_INVALID_PARAMETER);
        return;
    }

    device = WdfIoQueueGetDevice(Queue);
    context = ComoteVhidGetContext(device);
    if (context->VhfHandle == NULL) {
        WdfRequestComplete(Request, STATUS_DEVICE_NOT_READY);
        return;
    }

    RtlZeroMemory(&packet, sizeof(packet));
    packet.reportId = reportBuffer[0];
    packet.reportBuffer = reportBuffer;
    packet.reportBufferLen = (ULONG)reportLength;

    status = VhfReadReportSubmit(context->VhfHandle, &packet);
    WdfRequestComplete(Request, status);
}
