using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.Dota.Internal;

namespace DotaInviteHelper.Services
{
	public class DotaTeamService
	{
		DotaClient dota;
		const int APPID = 570;
		Dictionary<uint, Action<object>> messageMap;

		bool spamRunning;
		bool spamAutoKick;
		const int InitialInviteBurstCount = 5;

		public delegate void TeamInfoResponse(ClientGCMsgProtobuf<CMsgDOTATeamsInfo> response);
		public event TeamInfoResponse teamInfo;

		public delegate void EditTeamResponse(ClientGCMsgProtobuf<CMsgDOTAEditTeamDetailsResponse> response);
		public event EditTeamResponse teamEdited;

		public delegate void CreateTeamResponse(ClientGCMsgProtobuf<CMsgDOTACreateTeamResponse> response);
		public event CreateTeamResponse teamCreated;

		public DotaTeamService(DotaClient _dota)
		{
			dota = _dota;
			dota.GCMesage += Dota_GCMesage;
			messageMap = new Dictionary<uint, Action<object>>
			{
				{ ( uint )EDOTAGCMsg.k_EMsgGCToClientTeamsInfo, (r) =>  teamInfo(new ClientGCMsgProtobuf<CMsgDOTATeamsInfo>((IPacketGCMsg)r))},
				{ ( uint )EDOTAGCMsg.k_EMsgGCEditTeamDetailsResponse, (r) =>  teamEdited(new ClientGCMsgProtobuf<CMsgDOTAEditTeamDetailsResponse>((IPacketGCMsg)r))},
				{ ( uint )EDOTAGCMsg.k_EMsgGCCreateTeamResponse, (r) =>  teamCreated(new ClientGCMsgProtobuf<CMsgDOTACreateTeamResponse>((IPacketGCMsg)r))},
			};
		}

		uint spamTargetId;
		uint targetTeamId;

		void SendInvite()
		{
			if (!spamRunning || !dota.IsLoaded)
			{
				dota.WriteLog($"[INVITE] skipped: running={spamRunning}, dotaLoaded={dota.IsLoaded}");
				return;
			}

			var inviteRequest = new ClientGCMsgProtobuf<CMsgDOTATeamInvite_InviterToGC>((uint)EDOTAGCMsg.k_EMsgGCTeamInvite_InviterToGC);
			inviteRequest.Body.team_id = targetTeamId;
			inviteRequest.Body.account_id = spamTargetId;
			dota.WriteLog($"[INVITE] send team={targetTeamId}, account={spamTargetId}");
			dota.gameCoordinator.Send(inviteRequest, APPID);
		}

		public void StartSpam(uint team_id, uint accountId, bool autoKick)
		{
			spamTargetId = accountId;
			targetTeamId = team_id;
			spamAutoKick = autoKick;
			spamRunning = true;

			dota.GCMesage += autoKick
				? OnGCMessageWhenSpamWithAutoKick
				: OnGCMessageWhenSpam;

			// Queue several requests immediately so multiple invites can be in flight.
			for (var i = 0; i < InitialInviteBurstCount; i++)
				SendInvite();
		}

		public void StopSpam(bool autoKick)
		{
			spamRunning = false;

			// Use the mode that was used at start time. The checkbox can change
			// while the spammer is running, and unsubscribing with the new value
			// would leave the old callback attached.
			dota.GCMesage -= spamAutoKick
				? OnGCMessageWhenSpamWithAutoKick
				: OnGCMessageWhenSpam;
		}

		public void OnGCMessageWhenSpamWithAutoKick(SteamGameCoordinator.MessageCallback callback)
		{
			dota.WriteLog($"[SPAM-AUTOKICK] GC message: {callback.EMsg} / {(EDOTAGCMsg)callback.EMsg}");
			var inviteeResponse = LogInviteeResponse(callback);
			if (inviteeResponse.HasValue)
			{
				if (inviteeResponse.Value == ETeamInviteResult.TEAM_INVITE_SUCCESS)
				{
					SendKick();
					SendInvite();
				}
				else
					SendInvite();

				return;
			}

			if (callback.EMsg == (uint)EDOTAGCMsg.k_EMsgGCKickTeamMemberResponse)
			{
				var result = new ClientGCMsgProtobuf<CMsgDOTAKickTeamMemberResponse>(callback.Message);
				dota.WriteLog($"[INVITE] kick result={result.Body.result}");
				SendInvite();
				return;
			}
			
			if (callback.EMsg == (uint)EDOTAGCMsg.k_EMsgGCTeamInvite_GCImmediateResponseToInviter)
			{
				var result = new ClientGCMsgProtobuf<CMsgDOTATeamInvite_GCImmediateResponseToInviter>(callback.Message);
				dota.WriteLog($"[INVITE] result={result.Body.result}, name={result.Body.invitee_name}, requiredPlayTime={result.Body.required_play_time}");
				if (result.Body.result == ETeamInviteResult.TEAM_INVITE_ERROR_INVITEE_ALREADY_MEMBER)
				{
					SendKick();
				}

				// Reproduce the original Dota bug workaround: send the next
				// invite immediately after the GC response, without a timer.
				SendInvite();
			}
		}

		public void OnGCMessageWhenSpam(SteamGameCoordinator.MessageCallback callback)
		{
			if (LogInviteeResponse(callback).HasValue)
			{
				SendInvite();
				return;
			}

			if (callback.EMsg ==
				(uint)EDOTAGCMsg.k_EMsgGCTeamInvite_GCImmediateResponseToInviter)
			{
				var result =
					new ClientGCMsgProtobuf<CMsgDOTATeamInvite_GCImmediateResponseToInviter>(
						callback.Message);

				dota.WriteLog(
					$"[INVITE] result={result.Body.result}, " +
					$"name={result.Body.invitee_name}, " +
					$"requiredPlayTime={result.Body.required_play_time}"
				);

				SendInvite();
			}
		}

		ETeamInviteResult? LogInviteeResponse(SteamGameCoordinator.MessageCallback callback)
		{
			if (callback.EMsg != (uint)EDOTAGCMsg.k_EMsgGCTeamInvite_GCResponseToInviter)
				return null;

			var result = new ClientGCMsgProtobuf<CMsgDOTATeamInvite_GCResponseToInviter>(callback.Message);
			dota.WriteLog($"[INVITE] invitee response={result.Body.result}, name={result.Body.invitee_name}");
			return result.Body.result;
		}

		void SendKick()
		{
			var kickRequest = new ClientGCMsgProtobuf<CMsgDOTAKickTeamMember>((uint)EDOTAGCMsg.k_EMsgGCKickTeamMember);
			kickRequest.Body.account_id = spamTargetId;
			kickRequest.Body.team_id = targetTeamId;
			dota.WriteLog($"[INVITE] kick team={targetTeamId}, account={spamTargetId}");
			dota.gameCoordinator.Send(kickRequest, APPID);
		}
		
		public void LoadTeams()
		{
			var requestTeams = new ClientGCMsgProtobuf<CMsgDOTAMyTeamInfoRequest>((uint)EDOTAGCMsg.k_EMsgClientToGCMyTeamInfoRequest);
			dota.gameCoordinator.Send(requestTeams, APPID);
		}

		public void ChangeName(string name, uint team_id)
		{
			var editDetails = new ClientGCMsgProtobuf<CMsgDOTAEditTeamDetails>((uint)EDOTAGCMsg.k_EMsgGCEditTeamDetails);
			editDetails.Body.name = name;
			editDetails.Body.team_id = team_id;
			dota.gameCoordinator.Send(editDetails, APPID);
		}

		public void CreateTeam(string name)
		{
			var newTeam = new ClientGCMsgProtobuf<CMsgDOTACreateTeam>((uint)EDOTAGCMsg.k_EMsgGCCreateTeam);
			newTeam.Body.name = name;
			dota.gameCoordinator.Send(newTeam, APPID);
		}

		private void Dota_GCMesage(SteamKit2.SteamGameCoordinator.MessageCallback callback)
		{
			Action<object> func;
			
			if (!messageMap.TryGetValue(callback.EMsg, out func))
				return;

			func(callback.Message);
		}
	}
}
