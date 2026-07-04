#pragma once

#include <ntddk.h>

#define CNT_DEVICE_NAME L"\\Device\\CampusNetTrafficNet"
#define CNT_SYMBOLIC_LINK_NAME L"\\DosDevices\\CampusNetTrafficNet"
#define CNT_DOS_DEVICE_NAME L"\\\\.\\CampusNetTrafficNet"

#define CNT_DEVICE_TYPE 0x8337
#define IOCTL_CNT_GET_COUNTERS CTL_CODE(CNT_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_CNT_RESET_COUNTERS CTL_CODE(CNT_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_WRITE_DATA)

typedef struct _CNT_COUNTERS {
    ULONG64 InboundBytes;
    ULONG64 OutboundBytes;
} CNT_COUNTERS, *PCNT_COUNTERS;
