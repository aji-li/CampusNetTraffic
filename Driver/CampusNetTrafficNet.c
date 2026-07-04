#include "CampusNetTrafficNet.h"

#include <initguid.h>
#include <fwpmk.h>
#include <fwpsk.h>
#include <ndis.h>

// {A9E6E5A3-ED40-44BC-8C96-7768DF67AD0A}
DEFINE_GUID(CNT_PROVIDER_KEY,
            0xa9e6e5a3, 0xed40, 0x44bc, 0x8c, 0x96, 0x77, 0x68, 0xdf, 0x67, 0xad, 0x0a);
// {CA97A15B-B62D-4E41-9AA8-7DF11D858C02}
DEFINE_GUID(CNT_CALLOUT_INBOUND_V4,
            0xca97a15b, 0xb62d, 0x4e41, 0x9a, 0xa8, 0x7d, 0xf1, 0x1d, 0x85, 0x8c, 0x02);
// {4D8ED457-D719-47F6-BCC2-8570484D8D5F}
DEFINE_GUID(CNT_CALLOUT_OUTBOUND_V4,
            0x4d8ed457, 0xd719, 0x47f6, 0xbc, 0xc2, 0x85, 0x70, 0x48, 0x4d, 0x8d, 0x5f);
// {25E8FD51-E7DA-4B32-8DD0-F0E6469515CE}
DEFINE_GUID(CNT_CALLOUT_INBOUND_V6,
            0x25e8fd51, 0xe7da, 0x4b32, 0x8d, 0xd0, 0xf0, 0xe6, 0x46, 0x95, 0x15, 0xce);
// {4380D30C-B3F4-487E-9E16-8500DD03F946}
DEFINE_GUID(CNT_CALLOUT_OUTBOUND_V6,
            0x4380d30c, 0xb3f4, 0x487e, 0x9e, 0x16, 0x85, 0x00, 0xdd, 0x03, 0xf9, 0x46);

typedef enum _CNT_DIRECTION {
    CntDirectionInbound = 1,
    CntDirectionOutbound = 2
} CNT_DIRECTION;

static PDEVICE_OBJECT g_DeviceObject;
static HANDLE g_EngineHandle;
static UINT32 g_CalloutIds[4];
static volatile LONG64 g_InboundBytes;
static volatile LONG64 g_OutboundBytes;

static ULONG64 CntGetNetBufferListByteCount(_In_ NET_BUFFER_LIST* netBufferList)
{
    ULONG64 bytes = 0;

    for (NET_BUFFER* netBuffer = NET_BUFFER_LIST_FIRST_NB(netBufferList);
         netBuffer != NULL;
         netBuffer = NET_BUFFER_NEXT_NB(netBuffer)) {
        bytes += NET_BUFFER_DATA_LENGTH(netBuffer);
    }

    return bytes;
}

static void NTAPI CntClassify(
    _In_ const FWPS_INCOMING_VALUES0* inFixedValues,
    _In_ const FWPS_INCOMING_METADATA_VALUES0* inMetaValues,
    _Inout_opt_ void* layerData,
    _In_ const FWPS_FILTER0* filter,
    _In_ UINT64 flowContext,
    _Inout_ FWPS_CLASSIFY_OUT0* classifyOut)
{
    UNREFERENCED_PARAMETER(inFixedValues);
    UNREFERENCED_PARAMETER(inMetaValues);
    UNREFERENCED_PARAMETER(flowContext);

    if (layerData != NULL) {
        ULONG64 bytes = CntGetNetBufferListByteCount((NET_BUFFER_LIST*)layerData);
        if (filter->context == CntDirectionInbound) {
            InterlockedAdd64(&g_InboundBytes, (LONG64)bytes);
        } else if (filter->context == CntDirectionOutbound) {
            InterlockedAdd64(&g_OutboundBytes, (LONG64)bytes);
        }
    }

    classifyOut->actionType = FWP_ACTION_PERMIT;
}

static NTSTATUS NTAPI CntNotify(
    _In_ FWPS_CALLOUT_NOTIFY_TYPE notifyType,
    _In_ const GUID* filterKey,
    _Inout_ FWPS_FILTER0* filter)
{
    UNREFERENCED_PARAMETER(notifyType);
    UNREFERENCED_PARAMETER(filterKey);
    UNREFERENCED_PARAMETER(filter);
    return STATUS_SUCCESS;
}

static void NTAPI CntFlowDelete(_In_ UINT16 layerId, _In_ UINT32 calloutId, _In_ UINT64 flowContext)
{
    UNREFERENCED_PARAMETER(layerId);
    UNREFERENCED_PARAMETER(calloutId);
    UNREFERENCED_PARAMETER(flowContext);
}

static NTSTATUS CntRegisterOneCallout(
    _In_ const GUID* calloutKey,
    _In_ const GUID* layerKey,
    _In_ CNT_DIRECTION direction,
    _Out_ UINT32* calloutId)
{
    FWPS_CALLOUT0 sCallout = {0};
    FWPM_CALLOUT0 mCallout = {0};
    FWPM_FILTER0 filter = {0};
    NTSTATUS status;

    sCallout.calloutKey = *calloutKey;
    sCallout.classifyFn = CntClassify;
    sCallout.notifyFn = CntNotify;
    sCallout.flowDeleteFn = CntFlowDelete;

    status = FwpsCalloutRegister0(g_DeviceObject, &sCallout, calloutId);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    mCallout.calloutKey = *calloutKey;
    mCallout.displayData.name = L"CampusNetTraffic byte counter";
    mCallout.displayData.description = L"Counts network bytes for CAUCNet Traffic";
    mCallout.applicableLayer = *layerKey;

    status = FwpmCalloutAdd0(g_EngineHandle, &mCallout, NULL, NULL);
    if (!NT_SUCCESS(status)) {
        FwpsCalloutUnregisterById0(*calloutId);
        *calloutId = 0;
        return status;
    }

    filter.layerKey = *layerKey;
    filter.displayData.name = L"CampusNetTraffic byte counter filter";
    filter.displayData.description = L"Counts network bytes without blocking traffic";
    filter.action.type = FWP_ACTION_CALLOUT_INSPECTION;
    filter.action.calloutKey = *calloutKey;
    filter.rawContext = direction;
    filter.weight.type = FWP_EMPTY;
    filter.numFilterConditions = 0;
    filter.filterCondition = NULL;

    status = FwpmFilterAdd0(g_EngineHandle, &filter, NULL, NULL);
    if (!NT_SUCCESS(status)) {
        FwpsCalloutUnregisterById0(*calloutId);
        *calloutId = 0;
    }

    return status;
}

static NTSTATUS CntRegisterWfp()
{
    FWPM_SESSION0 session = {0};
    FWPM_PROVIDER0 provider = {0};
    NTSTATUS status;

    session.flags = FWPM_SESSION_FLAG_DYNAMIC;

    status = FwpmEngineOpen0(NULL, RPC_C_AUTHN_WINNT, NULL, &session, &g_EngineHandle);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    provider.providerKey = CNT_PROVIDER_KEY;
    provider.displayData.name = L"CampusNetTraffic";
    provider.displayData.description = L"CAUCNet Traffic WFP provider";
    (void)FwpmProviderAdd0(g_EngineHandle, &provider, NULL);

    status = FwpmTransactionBegin0(g_EngineHandle, 0);
    if (!NT_SUCCESS(status)) {
        FwpmEngineClose0(g_EngineHandle);
        g_EngineHandle = NULL;
        return status;
    }

    status = CntRegisterOneCallout(&CNT_CALLOUT_INBOUND_V4, &FWPM_LAYER_INBOUND_TRANSPORT_V4, CntDirectionInbound, &g_CalloutIds[0]);
    if (NT_SUCCESS(status)) {
        status = CntRegisterOneCallout(&CNT_CALLOUT_OUTBOUND_V4, &FWPM_LAYER_OUTBOUND_TRANSPORT_V4, CntDirectionOutbound, &g_CalloutIds[1]);
    }
    if (NT_SUCCESS(status)) {
        status = CntRegisterOneCallout(&CNT_CALLOUT_INBOUND_V6, &FWPM_LAYER_INBOUND_TRANSPORT_V6, CntDirectionInbound, &g_CalloutIds[2]);
    }
    if (NT_SUCCESS(status)) {
        status = CntRegisterOneCallout(&CNT_CALLOUT_OUTBOUND_V6, &FWPM_LAYER_OUTBOUND_TRANSPORT_V6, CntDirectionOutbound, &g_CalloutIds[3]);
    }

    if (NT_SUCCESS(status)) {
        status = FwpmTransactionCommit0(g_EngineHandle);
    } else {
        (void)FwpmTransactionAbort0(g_EngineHandle);
    }

    if (!NT_SUCCESS(status)) {
        for (UINT32 i = 0; i < RTL_NUMBER_OF(g_CalloutIds); i++) {
            if (g_CalloutIds[i] != 0) {
                FwpsCalloutUnregisterById0(g_CalloutIds[i]);
                g_CalloutIds[i] = 0;
            }
        }
        FwpmEngineClose0(g_EngineHandle);
        g_EngineHandle = NULL;
    }

    return status;
}

static void CntUnregisterWfp()
{
    if (g_EngineHandle != NULL) {
        FwpmEngineClose0(g_EngineHandle);
        g_EngineHandle = NULL;
    }

    for (UINT32 i = 0; i < RTL_NUMBER_OF(g_CalloutIds); i++) {
        if (g_CalloutIds[i] != 0) {
            FwpsCalloutUnregisterById0(g_CalloutIds[i]);
            g_CalloutIds[i] = 0;
        }
    }
}

static NTSTATUS CntCreateClose(_In_ PDEVICE_OBJECT deviceObject, _Inout_ PIRP irp)
{
    UNREFERENCED_PARAMETER(deviceObject);
    irp->IoStatus.Status = STATUS_SUCCESS;
    irp->IoStatus.Information = 0;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

static NTSTATUS CntDeviceControl(_In_ PDEVICE_OBJECT deviceObject, _Inout_ PIRP irp)
{
    UNREFERENCED_PARAMETER(deviceObject);

    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(irp);
    ULONG code = stack->Parameters.DeviceIoControl.IoControlCode;
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR information = 0;

    if (code == IOCTL_CNT_GET_COUNTERS) {
        if (stack->Parameters.DeviceIoControl.OutputBufferLength >= sizeof(CNT_COUNTERS)) {
            PCNT_COUNTERS counters = (PCNT_COUNTERS)irp->AssociatedIrp.SystemBuffer;
            counters->InboundBytes = (ULONG64)InterlockedCompareExchange64(&g_InboundBytes, 0, 0);
            counters->OutboundBytes = (ULONG64)InterlockedCompareExchange64(&g_OutboundBytes, 0, 0);
            information = sizeof(CNT_COUNTERS);
            status = STATUS_SUCCESS;
        } else {
            status = STATUS_BUFFER_TOO_SMALL;
        }
    } else if (code == IOCTL_CNT_RESET_COUNTERS) {
        InterlockedExchange64(&g_InboundBytes, 0);
        InterlockedExchange64(&g_OutboundBytes, 0);
        status = STATUS_SUCCESS;
    }

    irp->IoStatus.Status = status;
    irp->IoStatus.Information = information;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return status;
}

static void CntUnload(_In_ PDRIVER_OBJECT driverObject)
{
    UNICODE_STRING symbolicLink;

    CntUnregisterWfp();

    RtlInitUnicodeString(&symbolicLink, CNT_SYMBOLIC_LINK_NAME);
    IoDeleteSymbolicLink(&symbolicLink);

    if (driverObject->DeviceObject != NULL) {
        IoDeleteDevice(driverObject->DeviceObject);
    }
}

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT driverObject, _In_ PUNICODE_STRING registryPath)
{
    UNREFERENCED_PARAMETER(registryPath);

    UNICODE_STRING deviceName;
    UNICODE_STRING symbolicLink;
    NTSTATUS status;

    RtlInitUnicodeString(&deviceName, CNT_DEVICE_NAME);
    status = IoCreateDevice(driverObject, 0, &deviceName, CNT_DEVICE_TYPE, FILE_DEVICE_SECURE_OPEN, FALSE, &g_DeviceObject);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&symbolicLink, CNT_SYMBOLIC_LINK_NAME);
    status = IoCreateSymbolicLink(&symbolicLink, &deviceName);
    if (!NT_SUCCESS(status)) {
        IoDeleteDevice(g_DeviceObject);
        g_DeviceObject = NULL;
        return status;
    }

    for (UINT32 i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; i++) {
        driverObject->MajorFunction[i] = CntCreateClose;
    }
    driverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = CntDeviceControl;
    driverObject->DriverUnload = CntUnload;

    status = CntRegisterWfp();
    if (!NT_SUCCESS(status)) {
        CntUnload(driverObject);
        return status;
    }

    return STATUS_SUCCESS;
}
