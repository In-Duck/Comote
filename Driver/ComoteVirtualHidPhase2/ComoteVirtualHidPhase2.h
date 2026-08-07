#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <wdmsec.h>
#include <vhf.h>

#include "ComoteVirtualHidProtocol.h"

#define COMOTE_KEYBOARD_REPORT_ID 1U
#define COMOTE_MOUSE_REPORT_ID 2U
#define COMOTE_ABSOLUTE_MOUSE_REPORT_ID 3U

#pragma pack(push, 1)

typedef struct _COMOTE_KEYBOARD_REPORT
{
    UCHAR ReportId;
    UCHAR Modifiers;
    UCHAR Reserved;
    UCHAR Keys[COMOTE_KEYBOARD_MAX_KEYS];
} COMOTE_KEYBOARD_REPORT, *PCOMOTE_KEYBOARD_REPORT;

typedef struct _COMOTE_MOUSE_REPORT
{
    UCHAR ReportId;
    UCHAR Buttons;
    SHORT DeltaX;
    SHORT DeltaY;
    CHAR VerticalWheel;
    CHAR HorizontalWheel;
} COMOTE_MOUSE_REPORT, *PCOMOTE_MOUSE_REPORT;

typedef struct _COMOTE_ABSOLUTE_MOUSE_REPORT
{
    UCHAR ReportId;
    UCHAR Buttons;
    USHORT X;
    USHORT Y;
    CHAR VerticalWheel;
    CHAR HorizontalWheel;
} COMOTE_ABSOLUTE_MOUSE_REPORT, *PCOMOTE_ABSOLUTE_MOUSE_REPORT;

#pragma pack(pop)

C_ASSERT(sizeof(COMOTE_KEYBOARD_REPORT) == 9);
C_ASSERT(sizeof(COMOTE_MOUSE_REPORT) == 8);
C_ASSERT(sizeof(COMOTE_ABSOLUTE_MOUSE_REPORT) == 8);

typedef struct _COMOTE_DEVICE_CONTEXT
{
    VHFHANDLE VhfHandle;
    ULONG LastSequence;
    COMOTE_KEYBOARD_REPORT KeyboardState;
    UCHAR RelativeMouseButtons;
    UCHAR AbsoluteMouseButtons;
    USHORT AbsoluteMouseX;
    USHORT AbsoluteMouseY;
    BOOLEAN AbsoluteMouseInitialized;
    BOOLEAN ReleasePending;
} COMOTE_DEVICE_CONTEXT, *PCOMOTE_DEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(
    COMOTE_DEVICE_CONTEXT,
    ComoteGetDeviceContext);

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD ComoteEvtDeviceAdd;
EVT_WDF_DEVICE_CONTEXT_CLEANUP ComoteEvtDeviceCleanup;
EVT_WDF_DEVICE_FILE_CREATE ComoteEvtDeviceFileCreate;
EVT_WDF_FILE_CLEANUP ComoteEvtFileCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL ComoteEvtIoDeviceControl;
EVT_WDF_DEVICE_D0_ENTRY ComoteEvtDeviceD0Entry;
EVT_WDF_DEVICE_D0_EXIT ComoteEvtDeviceD0Exit;

