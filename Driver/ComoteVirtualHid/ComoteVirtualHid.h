#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <vhf.h>

typedef struct _COMOTE_DEVICE_CONTEXT
{
    VHFHANDLE VhfHandle;
} COMOTE_DEVICE_CONTEXT, *PCOMOTE_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(
    COMOTE_DEVICE_CONTEXT,
    ComoteGetDeviceContext);

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD ComoteEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP ComoteEvtDeviceCleanup;

