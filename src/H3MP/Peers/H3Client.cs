using BepInEx.Logging;
using Discord;
using FistVR;
using H3MP.Configs;
using H3MP.HarmonyPatches;
using H3MP.Messages;
using H3MP.Models;
using H3MP.Networking;
using H3MP.Utils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace H3MP.Peers
{
	internal delegate void OnH3ClientDisconnect(DisconnectInfo info);

	public class H3Client : Client<H3Client>, IRenderUpdatable
	{
		private const double PING_INTERVAL = 3;
		private const double HEALTH_INTERVAL = 60;

		private readonly ManualLogSource _log;
		private readonly ClientConfig _config;
		private readonly StatefulActivity _discord;
		private readonly OnH3ClientDisconnect _onDisconnected;

		private readonly double _tickDeltaTime;
		private readonly LoopTimer _tickTimer;

		private bool _disposed;
		private readonly Dictionary<byte, Puppet> _players;
		private readonly Dictionary<byte, Item> _items;
		private ServerTime _time;
		private HealthInfo _health;

		public double Time => _time?.Now ?? 0;

		internal H3Client(ManualLogSource log, ClientConfig config, StatefulActivity discord, PeerMessageList<H3Client> messages, byte channelsCount, double tickDeltaTime, Version version, IPEndPoint endpoint, ConnectionRequestMessage request, OnH3ClientDisconnect onDisconnected)
			: base(log, messages, channelsCount, new Events(), version, endpoint, x => x.Put(request))
		{
			_log = log;
			_config = config;
			_discord = discord;
			_onDisconnected = onDisconnected;

			_tickDeltaTime = tickDeltaTime;
			_tickTimer = new LoopTimer(tickDeltaTime);

			_players = new Dictionary<byte, Puppet>();
			_items = new Dictionary<byte, Item>();
			_health = new HealthInfo(HEALTH_INTERVAL, (int) (HEALTH_INTERVAL / PING_INTERVAL));
		}

		private void PrintHealth()
		{
			uint sent = _health.Sent;
			uint received = _health.Received;

			uint lost = sent - received;
			var loss = (float) lost / sent;

			double rttAvg = _time.Rtt;
			double offsetAvg = _time.Offset;
			DoubleRange offsetBounds = _time.OffsetBounds;

			double rttMad = _health.RttAbsoluteDeviation.Value;
			double rttMapd = rttMad / rttAvg;
			double offsetMad = _health.OffsetAbsoluteDeviation.Value;
			double offsetMapd = offsetMad / offsetAvg;

			// Yeah we could just format/concat and make it infinitely easier to read/write but the perf gaiiiinnnnsss
			var builder = new StringBuilder().AppendLine() // newline
				.Append("┌─CONNECTION HEALTH REPORT───").AppendLine()
				.Append("│       Packet─────loss : ").Append(loss.ToString("P1")).Append(" (").Append(lost).Append(" / ").Append(sent).AppendLine(")")
				.Append("│          RTT─┬──value : ").Append((rttAvg * 1000).ToString("N0")).AppendLine(" ms")
				.Append("│              ├────MAD : ").Append((rttMad * 1000).ToString(".0")).AppendLine(" ms")
				.Append("│              └───MAPD : ").AppendLine(rttMapd.ToString("P"))
				.Append("│ Clock offset─┬──value : ").Append(offsetAvg.ToString(".000")).AppendLine(" s")
				.Append("│              ├─bounds : ").Append(offsetBounds.Minimum.ToString(".000")).Append(" s <= x <= ").Append(offsetBounds.Maximum.ToString(".000")).AppendLine(" s")
				.Append("│              ├────MAD : ").Append(offsetMad.ToString(".000")).AppendLine(" s")
				.Append("│              └───MAPD : ").AppendLine(offsetMapd.ToString("P"))
				.Append("└────────────────────────────");
			_log.LogDebug(builder.ToString());

			_health.Sent = 0;
			_health.Received = 0;
		}

		private void OnPingSent()
		{
			++_health.Sent;
		}

		private void OnPingReceived(double offset, double rtt)
		{
			++_health.Received;
			_health.RttAbsoluteDeviation.Push(Math.Abs(_time.Rtt - rtt));
			_health.OffsetAbsoluteDeviation.Push(Math.Abs(_time.Offset - offset));

			if (!_health.DisplayTimer.TryCycle())
			{
				return;
			}

			PrintHealth();
		}

		private void UpdateDiscordPartySize(int size)
		{
			_discord.Update(x =>
			{
				x.Party.Size.CurrentSize = size;

				return x;
			});
		}

		private void AddPlayer(PlayerJoinMessage message)
		{
			var puppet = new Puppet(_log, _config.Puppet, () => _time, _tickDeltaTime);
			puppet.ProcessTransforms(message.Transforms);

			_players.Add(message.ID, puppet);
		}

		private void AddItem(ItemSpawnMessage message)
		{
			var item = new Item();
			item.ProcessTransforms(message.Transforms);

			_players.Add(message.ID, item);
		}

		private void LoadLevel(string name)
		{
			HarmonyState.LockLoadLevel = false;
			try
			{
				SteamVR_LoadLevel.Begin(name);
			}
			finally
			{
				HarmonyState.LockLoadLevel = true;
			}
		}

		public override void Update()
		{
			if (!_tickTimer.TryCycle())
			{
				return;
			}

			base.Update();

			// In case we disconnected.
			if (_disposed)
			{
				return;
			}

			if (!(_time is null))
			{
				_time.Update();

				var player = GM.CurrentPlayerBody;
				var transforms = new PlayerTransformsMessage(player.Head, player.LeftHand, player.RightHand);
				var timestamped = new Timestamped<PlayerTransformsMessage>(_time.Now, transforms);

				Server.Send(timestamped);
			}
		}

		public void RenderUpdate()
		{
			foreach (var player in _players.Values)
			{
				player.RenderUpdate();
			}
		}

		public override void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			base.Dispose();

			foreach (var player in _players.Values)
			{
				player.Dispose();
			}

			_discord.Update(x => default);

			_disposed = true;
		}

		internal static void OnServerPong(H3Client self, Peer peer, Timestamped<PingMessage> message)
		{
			if (self._time is null)
			{
				self._time = new ServerTime(self._log, peer, PING_INTERVAL, message);
				self._time.Sent += self.OnPingSent;
				self._time.Received += self.OnPingReceived;
			}
			else
			{
				self._time.ProcessPong(message);
			}
		}

		internal static void OnLevelChange(H3Client self, Peer peer, LevelChangeMessage message)
		{
			self.LoadLevel(message.Name);
		}

		internal static void OnInit(H3Client self, Peer peer, InitMessage message)
		{
			self._log.LogDebug("Initializing Discord party...");
			self._discord.Update(x =>
			{
				x.State = "In party";
				x.Party = new Discord.ActivityParty
				{
					Id = message.ID.ToString(),
					Size = new PartySize
					{
						CurrentSize = 1 + message.Players.Length,
						MaxSize = message.MaxSize
					}
				};
				x.Secrets.Join = message.Secret.ToBase64();

				return x;
			});

			self.LoadLevel(message.Level.Name);

			foreach (var player in message.Players)
			{
				self.AddPlayer(player);
			}
		}

		internal static void OnPlayerJoin(H3Client self, Peer peer, PlayerJoinMessage message)
		{
			self.AddPlayer(message);

			self.UpdateDiscordPartySize(1 + self._players.Count);
		}

		internal static void OnPlayerLeave(H3Client self, Peer peer, PlayerLeaveMessage message)
		{
			var id = message.ID;
			if (!self._players.TryGetValue(id, out var player))
			{
				return;
			}

			player.Dispose();
			self._players.Remove(id);

			self.UpdateDiscordPartySize(1 + self._players.Count);
		}

		internal static void OnPlayersMove(H3Client self, Peer peer, PlayerMovesMessage message)
		{
			foreach (KeyValuePair<byte, Timestamped<PlayerTransformsMessage>> delta in message.Players)
			{
				// In case the player hasn't spawned yet.
				if (self._players.TryGetValue(delta.Key, out var puppet))
				{
					puppet.ProcessTransforms(delta.Value);
				}
			}
		}

		//item events

		internal static void OnItemSpawn(H3Client self, Peer peer, ItemSpawnMessage message)
		{
			self.AddItem(message);
		}

		internal static void OnItemDespawn(H3Client self, Peer peer, ItemDespawnMessage message)
		{
			var id = message.ID;
			if (!self._items.TryGetValue(id, out var item))
			{
				return;
			}

			item.Dispose();
			self._items.Remove(id);

		}


		internal class Events : IClientEvents<H3Client>
		{
			public void OnConnected(H3Client client)
			{
			}

			public void OnDisconnected(H3Client client, DisconnectInfo info)
			{
				client._onDisconnected(info);
				client.Dispose();
			}
		}
	}
}
