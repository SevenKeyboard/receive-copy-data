;==============================================================
; ReceiveCopyData — WM_COPYDATA dispatcher with dwData-to-callback binding
;
; GitHub: https://github.com/SevenKeyboard/receive-copy-data
; Author: SevenKeyboard Ltd. (2025)
; License: The Unlicense
;==============================================================
class VersionManager_ReceiveCopyData
{
    static _ := VersionManager_ReceiveCopyData._init()
    _init()    {
        global
        RECEIVECOPYDATA_VERSION := "1.0.0"
    }
}
class ReceiveCopyData
{
    static _callback:=objBindMethod(ReceiveCopyData,"_onCopyData")
        ,_threadCount:=0
        ,_boundFuncList:={}
    callbackCreate(maxThreads:=1)    {
        static WM_COPYDATA:=0x004A
        if (this._threadCount<abs(maxThreads))
            onMessage(WM_COPYDATA, this._callback, maxThreads), this._threadCount:=abs(maxThreads)
    }
    callbackFree()    {
        static WM_COPYDATA:=0x004A
        if (this._threadCount)
            onMessage(WM_COPYDATA, this._callback, 0), this._threadCount:=0 
    }
    registerBoundFunc(dwData, byRef fn)    {
        this._boundFuncList[format("{:d}",dwData)]:=fn
    }
    unregisterBoundFunc(dwData)    {
        if (this._boundFuncList.hasKey(dwData:=format("{:d}",dwData)))
            this._boundFuncList.delete(dwData)
    }
    _onCopyData(wParam, lParam, Msg, hWnd)    {
        switch (A_PtrSize==8)
        {
            default:    ;  (32-bit AHK)  UInt ->
                for _ in ["lParam"] ;  Int
                    %_%:=%_%<<32>>32
            case true:  ;  (64-bit AHK)  Int64 ->
                for _ in ["wParam","msg"] ;  UInt, UInt
                    %_%&=0xFFFFFFFF
        }
        return (this._boundFuncList.hasKey(dwData:=numGet(lParam+0,"UPtr"))
            ?this._boundFuncList[dwData].call(wParam, lParam, Msg, hWnd)
            :true)
    }
}