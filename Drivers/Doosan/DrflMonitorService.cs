using System;
using System.Runtime.InteropServices;

using BODA.CMS.Drivers.Doosan.Drfl;

namespace BODA.CMS.Drivers.Doosan
{
    /// <summary>
    /// DRFL 기반 <b>패시브 모니터링</b> 서비스 (Phase 2 핵심 경로).
    ///
    /// ⚠️ 비개입 원칙: 이 서비스는 로봇의 명령 권한(access control)을 절대 가져오지 않는다.
    ///    _SetRobotControl / _SetRobotMode / _ManageAccessControl 를 호출하지 않으며,
    ///    오직 _SetOnMonitoringData 콜백으로 컨트롤러가 브로드캐스트하는 상태만 수동 수신한다.
    ///    → 로봇이 자기 프로그램을 돌리는 동안 생산 작업을 전혀 방해하지 않는다.
    ///
    /// 라이프사이클: Connect() → (네이티브 스레드에서) SampleReceived 이벤트 다수 → Disconnect().
    /// </summary>
    public sealed class DrflMonitorService : IDisposable
    {
        private IntPtr _ctrl = IntPtr.Zero;
        private bool _connected;

        // ⚠️ 델리게이트를 필드로 보관해 GC 수거를 막는다. 지역변수로 넘기면
        //    잠시 뒤 콜백이 발생할 때 이미 회수되어 CallbackOnCollectedDelegate 크래시가 난다.
        private TOnMonitoringDataCB? _onData;
        private TOnMonitoringStateCB? _onState;
        private TOnDisconnectedCB? _onDisconnected;

        /// <summary>모니터링 프레임 수신. ⚠️ 네이티브(DRFL) 스레드에서 호출되므로 구독자가 UI 마샬링 책임을 진다.</summary>
        public event Action<MonitoringSample>? SampleReceived;
        /// <summary>로봇 상태 변화(STANDBY/MOVING/...). 네이티브 스레드에서 호출.</summary>
        public event Action<ROBOT_STATE>? StateChanged;
        /// <summary>컨트롤러 연결 끊김 통지. 네이티브 스레드에서 호출.</summary>
        public event Action? Disconnected;

        public bool IsConnected => _connected;

        /// <summary>
        /// 컨트롤러에 접속하고 패시브 콜백을 등록한다.
        /// 콜백은 연결 <i>전</i>에 등록한다 — 접속 직후 첫 프레임부터 놓치지 않기 위해서.
        /// </summary>
        /// <param name="ip">컨트롤러 IP (기본 192.168.137.100).</param>
        /// <param name="port">DRFL 포트 (기본 12345).</param>
        public void Connect(string ip = "192.168.137.100", uint port = 12345)
        {
            if (_connected) return;

            _ctrl = DrflInterop.CreateRobotControl();
            if (_ctrl == IntPtr.Zero)
                throw new InvalidOperationException("DRFL 핸들 생성 실패(_CreateRobotControl). DLL 로드/아키텍처를 확인하세요.");

            // 델리게이트 인스턴스를 필드에 고정한 뒤 등록.
            _onData = OnMonitoringData;
            _onState = OnMonitoringState;
            _onDisconnected = OnDisconnected;

            DrflInterop.SetOnMonitoringData(_ctrl, _onData);
            DrflInterop.SetOnMonitoringState(_ctrl, _onState);
            DrflInterop.SetOnDisconnected(_ctrl, _onDisconnected);

            if (!DrflInterop.OpenConnection(_ctrl, ip, port))
            {
                DrflInterop.DestroyRobotControl(_ctrl);
                _ctrl = IntPtr.Zero;
                _onData = null; _onState = null; _onDisconnected = null;
                throw new InvalidOperationException($"DRFL 연결 실패(_OpenConnection): {ip}:{port}");
            }

            _connected = true;
        }

        /// <summary>시스템 버전 조회. DLL↔컨트롤러(DRCF V2.11.1) 호환 1회 대조용.</summary>
        public SYSTEM_VERSION GetSystemVersion()
        {
            EnsureConnected();
            var v = new SYSTEM_VERSION();
            if (!DrflInterop.GetSystemVersion(_ctrl, ref v))
                throw new InvalidOperationException("_GetSystemVersion 실패.");
            return v;
        }

        public ROBOT_STATE GetRobotState()
        {
            EnsureConnected();
            return DrflInterop.GetRobotState(_ctrl);
        }

        private void OnMonitoringData(IntPtr pData)
        {
            if (pData == IntPtr.Zero) return;
            MONITORING_DATA d = Marshal.PtrToStructure<MONITORING_DATA>(pData);

            var sample = new MonitoringSample
            {
                ReceivedAt = DateTime.Now,
                SyncTime = d._tMisc._dSyncTime,
                JointPosition = d._tCtrl._tJoint._fActualPos,
                JointVelocity = d._tCtrl._tJoint._fActualVel,
                DynamicTorque = d._tCtrl._tTorque._fDynamicTor,
                JointTorqueSensor = d._tCtrl._tTorque._fActualJTS,
                ExternalJointTorque = d._tCtrl._tTorque._fActualEJT,
                MotorCurrent = d._tMisc._fActualMC,
                MotorTemperature = d._tMisc._fActualMT,
            };
            SampleReceived?.Invoke(sample);
        }

        private void OnMonitoringState(ROBOT_STATE state) => StateChanged?.Invoke(state);
        private void OnDisconnected() => Disconnected?.Invoke();

        public void Disconnect()
        {
            if (_ctrl != IntPtr.Zero)
            {
                if (_connected) DrflInterop.CloseConnection(_ctrl);
                DrflInterop.DestroyRobotControl(_ctrl);
                _ctrl = IntPtr.Zero;
            }
            _connected = false;
            // 콜백 해제 후 델리게이트 참조 정리.
            _onData = null; _onState = null; _onDisconnected = null;
        }

        private void EnsureConnected()
        {
            if (!_connected || _ctrl == IntPtr.Zero)
                throw new InvalidOperationException("DRFL 에 연결되지 않았습니다.");
        }

        public void Dispose() => Disconnect();
    }
}
