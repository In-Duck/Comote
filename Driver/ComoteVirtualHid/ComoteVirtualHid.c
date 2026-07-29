#include "ComoteVirtualHid.h"

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, ComoteEvtDeviceAdd)
#pragma alloc_text(PAGE, ComoteEvtDeviceCleanup)
#endif

//
// Phase 1 descriptor only. No user-mode control interface and no input-report
// submission path are exposed in this build.
//
// Report 1: standard six-key-rollover keyboard without LED output reports.
// Report 2: five-button relative mouse with vertical and horizontal wheels.
//
static UCHAR ComoteReportDescriptor[] =
{
    // Keyboard top-level collection.
    0x05, 0x01,       // Usage Page (Generic Desktop)
    0x09, 0x06,       // Usage (Keyboard)
    0xA1, 0x01,       // Collection (Application)
    0x85, 0x01,       //   Report ID (1)
    0x05, 0x07,       //   Usage Page (Keyboard/Keypad)
    0x19, 0xE0,       //   Usage Minimum (Left Control)
    0x29, 0xE7,       //   Usage Maximum (Right GUI)
    0x15, 0x00,       //   Logical Minimum (0)
    0x25, 0x01,       //   Logical Maximum (1)
    0x75, 0x01,       //   Report Size (1)
    0x95, 0x08,       //   Report Count (8)
    0x81, 0x02,       //   Input (Data, Variable, Absolute)
    0x75, 0x08,       //   Report Size (8)
    0x95, 0x01,       //   Report Count (1)
    0x81, 0x03,       //   Input (Constant, Variable, Absolute)
    0x19, 0x00,       //   Usage Minimum (Reserved)
    0x29, 0xFF,       //   Usage Maximum (255)
    0x15, 0x00,       //   Logical Minimum (0)
    0x26, 0xFF, 0x00, //   Logical Maximum (255)
    0x75, 0x08,       //   Report Size (8)
    0x95, 0x06,       //   Report Count (6)
    0x81, 0x00,       //   Input (Data, Array, Absolute)
    0xC0,             // End Collection

    // Relative mouse top-level collection.
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
    0x75, 0x01,       //     Report Size (1)
    0x95, 0x05,       //     Report Count (5)
    0x81, 0x02,       //     Input (Data, Variable, Absolute)
    0x75, 0x03,       //     Report Size (3)
    0x95, 0x01,       //     Report Count (1)
    0x81, 0x03,       //     Input (Constant, Variable, Absolute)
    0x05, 0x01,       //     Usage Page (Generic Desktop)
    0x09, 0x30,       //     Usage (X)
    0x09, 0x31,       //     Usage (Y)
    0x16, 0x01, 0x80, //     Logical Minimum (-32767)
    0x26, 0xFF, 0x7F, //     Logical Maximum (32767)
    0x75, 0x10,       //     Report Size (16)
    0x95, 0x02,       //     Report Count (2)
    0x81, 0x06,       //     Input (Data, Variable, Relative)
    0x09, 0x38,       //     Usage (Wheel)
    0x15, 0x81,       //     Logical Minimum (-127)
    0x25, 0x7F,       //     Logical Maximum (127)
    0x75, 0x08,       //     Report Size (8)
    0x95, 0x01,       //     Report Count (1)
    0x81, 0x06,       //     Input (Data, Variable, Relative)
    0x05, 0x0C,       //     Usage Page (Consumer)
    0x0A, 0x38, 0x02, //     Usage (AC Pan)
    0x15, 0x81,       //     Logical Minimum (-127)
    0x25, 0x7F,       //     Logical Maximum (127)
    0x75, 0x08,       //     Report Size (8)
    0x95, 0x01,       //     Report Count (1)
    0x81, 0x06,       //     Input (Data, Variable, Relative)
    0xC0,             //   End Collection
    0xC0              // End Collection
};

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
    WDFDEVICE device;
    PCOMOTE_DEVICE_CONTEXT deviceContext;
    VHF_CONFIG vhfConfig;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Driver);
    PAGED_CODE();

    //
    // This parent device exposes no symbolic link or device interface. A later
    // phase will add one SYSTEM-only interface after its protocol is fuzzed.
    //
    WdfDeviceInitSetDeviceType(
        DeviceInit,
        FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetCharacteristics(
        DeviceInit,
        FILE_DEVICE_SECURE_OPEN,
        FALSE);
    WdfDeviceInitSetExclusive(
        DeviceInit,
        TRUE);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
        &deviceAttributes,
        COMOTE_DEVICE_CONTEXT);
    deviceAttributes.EvtCleanupCallback = ComoteEvtDeviceCleanup;
    deviceAttributes.ExecutionLevel = WdfExecutionLevelPassive;

    status = WdfDeviceCreate(
        &DeviceInit,
        &deviceAttributes,
        &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    deviceContext = ComoteGetDeviceContext(device);
    deviceContext->VhfHandle = NULL;

    VHF_CONFIG_INIT(
        &vhfConfig,
        WdfDeviceWdmGetDeviceObject(device),
        (USHORT)sizeof(ComoteReportDescriptor),
        ComoteReportDescriptor);

    //
    // No third-party USB VID/PID is imitated. These zero values are intentional
    // for the internal VHF prototype and must be revisited before distribution.
    //
    vhfConfig.VendorID = 0;
    vhfConfig.ProductID = 0;
    vhfConfig.VersionNumber = 1;

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
        //
        // Synchronous deletion at PASSIVE_LEVEL is the documented VHF cleanup
        // path and prevents the device context from outliving pending callbacks.
        //
        VhfDelete(
            deviceContext->VhfHandle,
            TRUE);
        deviceContext->VhfHandle = NULL;
    }
}
