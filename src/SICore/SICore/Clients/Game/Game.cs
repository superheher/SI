﻿using Notions;
using SICore.BusinessLogic;
using SICore.Network;
using SICore.Network.Clients;
using SICore.Network.Contracts;
using SICore.PlatformSpecific;
using SICore.Results;
using SICore.Special;
using SICore.Utils;
using SIData;
using SIPackages;
using SIPackages.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using R = SICore.Properties.Resources;

namespace SICore
{
    /// <summary>
    /// Defines a game actor. Responds to all game-related messages.
    /// </summary>
    public sealed class Game : Actor<GameData, GameLogic>
    {
        public event Action<bool, bool> PersonsChanged;
        public event Action<string> DisconnectRequested;

        /// <summary>
        /// Maximum avatar size in bytes.
        /// </summary>
        private const int MaxAvatarSize = 1024 * 1024;

        private readonly GameActions _gameActions;

        private IMasterServer MasterServer => (IMasterServer)_client.Server;

        private readonly ComputerAccount[] _defaultPlayers;
        private readonly ComputerAccount[] _defaultShowmans;

        public Game(
            Client client,
            string documentPath,
            ILocalizer localizer,
            GameData gameData,
            ComputerAccount[] defaultPlayers,
            ComputerAccount[] defaultShowmans)
            : base(client, localizer, gameData)
        {
            _gameActions = new GameActions(_client, ClientData, LO);
            _logic = CreateLogic(null);

            gameData.DocumentPath = documentPath;
            gameData.Share.Error += Share_Error;

            _defaultPlayers = defaultPlayers ?? throw new ArgumentNullException(nameof(defaultPlayers));
            _defaultShowmans = defaultShowmans ?? throw new ArgumentNullException(nameof(defaultShowmans));
        }

        protected override GameLogic CreateLogic(Account personData) => new(ClientData, _gameActions, LO, AutoGame);

        public override async ValueTask DisposeAsync(bool disposing)
        {
            ClientData.Share.Error -= Share_Error;
            ClientData.Share.Dispose();

            // Logic must be disposed before TaskLock
            await base.DisposeAsync(disposing);

            ClientData.TaskLock.Dispose();
            ClientData.TableInformStageLock.Dispose();
        }

        /// <summary>
        /// Запуск игры
        /// </summary>
        /// <param name="settings">Настройки</param>
        public void Run(SIDocument document)
        {
            Client.CurrentServer.SerializationError += CurrentServer_SerializationError;

            _logic.Run(document);

            foreach (var personName in ClientData.AllPersons.Keys)
            {
                if (personName == NetworkConstants.GameName)
                {
                    continue;
                }

                Inform(personName);
            }
        }

        private void CurrentServer_SerializationError(Message message, Exception exc)
        {
            // Это случается при выводе частичного текста. Пытаемся поймать
            try
            {
                var fullText = ClientData.Text ?? "";

                var errorMessage = new StringBuilder("SerializationError: ")
                    .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(fullText)))
                    .Append('\n')
                    .Append(ClientData.TextLength)
                    .Append('\n')
                    .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Sender)))
                    .Append('\n')
                    .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Receiver)))
                    .Append('\n')
                    .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(message.Text)))
                    .Append('\n')
                    .Append((message.Text ?? "").Length)
                    .Append(' ').Append(ClientData.Settings.AppSettings.ReadingSpeed);

                _client.Server.OnError(new Exception(errorMessage.ToString(), exc), true);
            }
            catch (Exception e)
            {
                _client.Server.OnError(e, true);
            }
        }

        private void Share_Error(Exception exc) => _client.Server.OnError(exc, true);

        /// <summary>
        /// Sends all current game data to the person.
        /// </summary>
        /// <param name="person">Receiver name.</param>
        private void Inform(string person = NetworkConstants.Everybody)
        {
            _gameActions.SendMessageToWithArgs(
                person,
                Messages.ComputerAccounts,
                string.Join(Message.ArgsSeparator, _defaultPlayers.Select(p => p.Name)));

            var info = new StringBuilder(Messages.Info2)
                .Append(Message.ArgsSeparatorChar)
                .Append(ClientData.Players.Count)
                .Append(Message.ArgsSeparatorChar);

            AppendAccountExt(ClientData.ShowMan, info);

            info.Append(Message.ArgsSeparatorChar);

            foreach (var player in ClientData.Players)
            {
                AppendAccountExt(player, info);

                info.Append(Message.ArgsSeparatorChar);
            }

            foreach (var viewer in ClientData.Viewers)
            {
                if (!viewer.IsConnected)
                {
                    ClientData.BackLink.LogWarning($"Viewer {viewer.Name} not connected\n" + ClientData.PersonsUpdateHistory);
                    continue;
                }

                AppendAccountExt(viewer, info);
                info.Append(Message.ArgsSeparatorChar);
            }

            var msg = info.ToString()[..(info.Length - 1)];

            _gameActions.SendMessage(msg, person);

            // Send persons avatars info
            if (person != NetworkConstants.Everybody)
            {
                InformPicture(ClientData.ShowMan, person);

                foreach (var item in ClientData.Players)
                {
                    InformPicture(item, person);
                }
            }
            else
            {
                InformPicture(ClientData.ShowMan);

                foreach (var item in ClientData.Players)
                {
                    InformPicture(item);
                }
            }

            _gameActions.SendMessageToWithArgs(
                person,
                Messages.ReadingSpeed,
                ClientData.Settings.AppSettings.Managed ? 0 : ClientData.Settings.AppSettings.ReadingSpeed);

            _gameActions.SendMessageToWithArgs(person, Messages.FalseStart, ClientData.Settings.AppSettings.FalseStart ? "+" : "-");
            _gameActions.SendMessageToWithArgs(person, Messages.ButtonBlockingTime, ClientData.Settings.AppSettings.TimeSettings.TimeForBlockingButton);
            _gameActions.SendMessageToWithArgs(person, Messages.ApellationEnabled, ClientData.Settings.AppSettings.UseApellations ? '+' : '-');

            var maxPressingTime = ClientData.Settings.AppSettings.TimeSettings.TimeForThinkingOnQuestion * 10;
            _gameActions.SendMessageToWithArgs(person, Messages.Timer, 1, "MAXTIME", maxPressingTime);
            _gameActions.SendMessageToWithArgs(person, Messages.Hostname, ClientData.HostName ?? "");
        }

        private static void AppendAccountExt(ViewerAccount account, StringBuilder info)
        {
            info.Append(account.Name);
            info.Append(Message.ArgsSeparatorChar);
            info.Append(account.IsMale ? '+' : '-');
            info.Append(Message.ArgsSeparatorChar);
            info.Append(account.IsConnected ? '+' : '-');
            info.Append(Message.ArgsSeparatorChar);
            info.Append(account.IsHuman ? '+' : '-');
            info.Append(Message.ArgsSeparatorChar);

            info.Append(account is GamePersonAccount person && person.Ready ? '+' : '-');
        }

        public string GetSums()
        {
            var s = new StringBuilder();
            var total = ClientData.Players.Count;

            for (int i = 0; i < total; i++)
            {
                if (s.Length > 0)
                {
                    s.Append(", ");
                }

                s.AppendFormat("{0}: {1}", ClientData.Players[i].Name, ClientData.Players[i].Sum);
            }

            return s.ToString();
        }

        public ConnectionPersonData[] GetInfo()
        {
            var result = new List<ConnectionPersonData>
            {
                new ConnectionPersonData { Name = ClientData.ShowMan.Name, Role = GameRole.Showman, IsOnline = ClientData.ShowMan.IsConnected }
            };

            for (int i = 0; i < ClientData.Players.Count; i++)
            {
                result.Add(new ConnectionPersonData
                {
                    Name = ClientData.Players[i].Name,
                    Role = GameRole.Player,
                    IsOnline = ClientData.Players[i].IsConnected
                });
            }

            for (int i = 0; i < ClientData.Viewers.Count; i++)
            {
                result.Add(new ConnectionPersonData
                {
                    Name = ClientData.Viewers[i].Name,
                    Role = GameRole.Viewer,
                    IsOnline = ClientData.Viewers[i].IsConnected
                });
            }

            return result.ToArray();
        }

        /// <summary>
        /// Adds person to the game.
        /// </summary>
        public (bool, string) Join(
            string name,
            bool isMale,
            GameRole role,
            string password,
            Action connectionAuthenticator) =>
            ClientData.TaskLock.WithLock(() =>
            {
                if (!string.IsNullOrEmpty(ClientData.Settings.NetworkGamePassword) &&
                    ClientData.Settings.NetworkGamePassword != password)
                {
                    return (false, LO[nameof(R.WrongPassword)]);
                }

                if (ClientData.AllPersons.ContainsKey(name))
                {
                    return (false, string.Format(LO[nameof(R.PersonWithSuchNameIsAlreadyInGame)], name));
                }

                var index = -1;
                IEnumerable<ViewerAccount> accountsToSearch = null;

                switch (role)
                {
                    case GameRole.Showman:
                        accountsToSearch = new ViewerAccount[1] { ClientData.ShowMan };
                        break;

                    case GameRole.Player:
                        accountsToSearch = ClientData.Players;

                        if (ClientData.HostName == name) // Host is joining
                        {
                            var players = ClientData.Players;

                            for (var i = 0; i < players.Count; i++)
                            {
                                if (players[i].Name == name)
                                {
                                    index = i;
                                    break;
                                }
                            }

                            if (index < 0)
                            {
                                return (false, LO[nameof(R.PositionNotFoundByIndex)]);
                            }
                        }

                        break;

                    default: // Viewer
                        accountsToSearch = ClientData.Viewers.Concat(
                            new ViewerAccount[] { new ViewerAccount(Constants.FreePlace, false, false) { IsHuman = true } });

                        break;
                }

                var found = false;

                if (index > -1)
                {
                    var accounts = accountsToSearch.ToArray();

                    var result = CheckAccountNew(
                        role.ToString().ToLower(),
                        name,
                        isMale ? "m" : "f",
                        ref found,
                        index,
                        accounts[index],
                        connectionAuthenticator);

                    if (result.HasValue)
                    {
                        if (!result.Value)
                        {
                            return (false, LO[nameof(R.PlaceIsOccupied)]);
                        }
                        else
                        {
                            found = true;
                        }
                    }
                }
                else
                {
                    foreach (var item in accountsToSearch)
                    {
                        index++;

                        var result = CheckAccountNew(
                            role.ToString().ToLower(),
                            name,
                            isMale ? "m" : "f",
                            ref found,
                            index,
                            item,
                            connectionAuthenticator);

                        if (result.HasValue)
                        {
                            if (!result.Value)
                            {
                                return (false, LO[nameof(R.PlaceIsOccupied)]);
                            }
                            else
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                }

                if (!found)
                {
                    return (false, LO[nameof(R.NoFreePlaceForName)]);
                }

                return (true, "");
            },
            5000);

        /// <summary>
        /// Processed received message.
        /// </summary>
        /// <param name="message">Received message.</param>
        public override ValueTask OnMessageReceivedAsync(Message message) =>
            ClientData.TaskLock.WithLockAsync(async () =>
            {
                if (string.IsNullOrEmpty(message.Text))
                {
                    return;
                }

                Logic.AddHistory($"[{message.Text}@{message.Sender}]");

                var args = message.Text.Split(Message.ArgsSeparatorChar);

                try
                {
                    var res = new StringBuilder();
                    // Action according to protocol
                    switch (args[0])
                    {
                        case Messages.GameInfo:
                            #region GameInfo

                            // Информация о текущей игре для подключающихся по сети
                            res.Append(Messages.GameInfo);
                            res.Append(Message.ArgsSeparatorChar).Append(ClientData.Settings.NetworkGameName);
                            res.Append(Message.ArgsSeparatorChar).Append(ClientData.HostName);
                            res.Append(Message.ArgsSeparatorChar).Append(ClientData.Players.Count);

                            res.Append(Message.ArgsSeparatorChar).Append(ClientData.ShowMan.Name);
                            res.Append(Message.ArgsSeparatorChar).Append(ClientData.ShowMan.IsConnected ? '+' : '-');
                            res.Append(Message.ArgsSeparatorChar).Append('-');

                            for (int i = 0; i < ClientData.Players.Count; i++)
                            {
                                res.Append(Message.ArgsSeparatorChar).Append(ClientData.Players[i].Name);
                                res.Append(Message.ArgsSeparatorChar).Append(ClientData.Players[i].IsConnected ? '+' : '-');
                                res.Append(Message.ArgsSeparatorChar).Append('-');
                            }

                            for (int i = 0; i < ClientData.Viewers.Count; i++)
                            {
                                res.Append(Message.ArgsSeparatorChar).Append(ClientData.Viewers[i].Name);
                                res.Append(Message.ArgsSeparatorChar).Append(ClientData.Viewers[i].IsConnected ? '+' : '-');
                                res.Append(Message.ArgsSeparatorChar).Append('-');
                            }

                            _gameActions.SendMessage(res.ToString(), message.Sender);

                            #endregion
                            break;

                        case Messages.Connect:
                            await OnConnectAsync(message, args);
                            break;

                        case SystemMessages.Disconnect:
                            OnDisconnect(args);
                            break;

                        case Messages.Info:
                            OnInfo(message);
                            break;

                        case Messages.Config:
                            ProcessConfig(message, args);
                            break;

                        case Messages.First:
                            if (ClientData.IsWaiting &&
                                ClientData.Decision == DecisionType.StarterChoosing &&
                                message.Sender == ClientData.ShowMan.Name &&
                                args.Length > 1)
                            {
                                #region First
                                // Ведущий прислал номер того, кто начнёт игру
                                if (int.TryParse(args[1], out int playerIndex) && playerIndex > -1 && playerIndex < ClientData.Players.Count && ClientData.Players[playerIndex].Flag)
                                {
                                    ClientData.ChooserIndex = playerIndex;
                                    _logic.Stop(StopReason.Decision);
                                }
                                #endregion
                            }
                            break;

                        case Messages.Pause:
                            OnPause(message, args);
                            break;

                        case Messages.Start:
                            if (message.Sender == ClientData.HostName && ClientData.Stage == GameStage.Before)
                            {
                                StartGame();
                            }
                            break;

                        case Messages.Ready:
                            OnReady(message, args);
                            break;

                        case Messages.Picture:
                            OnPicture(message, args);
                            break;

                        case Messages.Choice:
                            if (ClientData.IsWaiting &&
                                ClientData.Decision == DecisionType.QuestionChoosing &&
                                args.Length == 3 &&
                                ClientData.Chooser != null &&
                                    (message.Sender == ClientData.Chooser.Name ||
                                    ClientData.IsOralNow && message.Sender == ClientData.ShowMan.Name))
                            {
                                #region Choice

                                if (!int.TryParse(args[1], out int i) || !int.TryParse(args[2], out int j))
                                {
                                    break;
                                }

                                if (i < 0 || i >= ClientData.TInfo.RoundInfo.Count)
                                {
                                    break;
                                }

                                if (j < 0 || j >= ClientData.TInfo.RoundInfo[i].Questions.Count)
                                {
                                    break;
                                }

                                if (ClientData.TInfo.RoundInfo[i].Questions[j].IsActive())
                                {
                                    lock (ClientData.ChoiceLock)
                                    {
                                        ClientData.ThemeIndex = i;
                                        ClientData.QuestionIndex = j;
                                    }

                                    if (ClientData.IsOralNow)
                                        _gameActions.SendMessage(Messages.Cancel, message.Sender == ClientData.ShowMan.Name ?
                                            ClientData.Chooser.Name : ClientData.ShowMan.Name);

                                    _logic.Stop(StopReason.Decision);
                                }

                                #endregion
                            }
                            break;

                        case Messages.I:
                            OnI(message.Sender);
                            break;

                        case Messages.Pass:
                            OnPass(message);
                            break;

                        case Messages.Answer:
                            OnAnswer(message, args);
                            break;

                        case Messages.Atom:
                            OnAtom();
                            break;

                        case Messages.Report:
                            #region Report
                            if (ClientData.Decision == DecisionType.Reporting)
                            {
                                ClientData.ReportsCount--;
                                if (args.Length > 2)
                                {
                                    if (ClientData.GameResultInfo.Comments.Length > 0)
                                        ClientData.GameResultInfo.Comments += Environment.NewLine;

                                    ClientData.GameResultInfo.Comments += args[2];
                                    ClientData.AcceptedReports++;
                                }

                                if (ClientData.ReportsCount == 0)
                                    _logic.ExecuteImmediate();
                            }
                            break;
                        #endregion

                        case Messages.IsRight:
                            OnIsRight(message, args);
                            break;

                        case Messages.Next:
                            if (ClientData.IsWaiting &&
                                ClientData.Decision == DecisionType.NextPersonStakeMaking &&
                                message.Sender == ClientData.ShowMan.Name)
                            {
                                #region Next

                                if (args.Length > 1 && int.TryParse(args[1], out int n) && n > -1 && n < ClientData.Players.Count)
                                {
                                    if (ClientData.Players[n].Flag)
                                    {
                                        ClientData.Order[ClientData.OrderIndex] = n;
                                        Logic.CheckOrder(ClientData.OrderIndex);
                                        _logic.Stop(StopReason.Decision);
                                    }
                                }

                                #endregion
                            }
                            break;

                        case Messages.Cat:
                            if (ClientData.IsWaiting &&
                                ClientData.Decision == DecisionType.CatGiving &&
                                (ClientData.Chooser != null && message.Sender == ClientData.Chooser.Name ||
                                ClientData.IsOralNow && message.Sender == ClientData.ShowMan.Name))
                            {
                                #region Cat

                                try
                                {
                                    if (int.TryParse(args[1], out int index) && index > -1 && index < ClientData.Players.Count && ClientData.Players[index].Flag)
                                    {
                                        ClientData.AnswererIndex = index;

                                        if (ClientData.IsOralNow)
                                            _gameActions.SendMessage(Messages.Cancel, message.Sender == ClientData.ShowMan.Name ?
                                                ClientData.Chooser.Name : ClientData.ShowMan.Name);

                                        _logic.Stop(StopReason.Decision);
                                    }
                                }
                                catch (Exception) { }

                                #endregion
                            }
                            break;

                        case Messages.CatCost:
                            OnCatCost(message, args);
                            break;

                        case Messages.Stake:
                            OnStake(message, args);
                            break;

                        case Messages.NextDelete:
                            OnNextDelete(message, args);
                            break;

                        case Messages.Delete:
                            OnDelete(message, args);
                            break;

                        case Messages.FinalStake:
                            if (ClientData.IsWaiting && ClientData.Decision == DecisionType.FinalStakeMaking)
                            {
                                #region FinalStake

                                for (var i = 0; i < ClientData.Players.Count; i++)
                                {
                                    var player = ClientData.Players[i];

                                    if (player.InGame && player.FinalStake == -1 && message.Sender == player.Name)
                                    {
                                        if (int.TryParse(args[1], out int finalStake) && finalStake >= 1 && finalStake <= player.Sum)
                                        {
                                            player.FinalStake = finalStake;
                                            ClientData.NumOfStakers--;

                                            _gameActions.SendMessageWithArgs(Messages.PersonFinalStake, i);
                                        }

                                        break;
                                    }
                                }

                                if (ClientData.NumOfStakers == 0)
                                {
                                    _logic.Stop(StopReason.Decision);
                                }

                                #endregion
                            }
                            break;

                        case Messages.Apellate:
                            OnApellation(message, args);
                            break;

                        case Messages.Change:
                            OnChanged(message, args);
                            break;

                        case Messages.Move:
                            OnMove(message, args);
                            break;

                        case Messages.Kick:
                            if (message.Sender == ClientData.HostName & args.Length > 1)
                            {
                                var person = args[1];

                                if (!ClientData.AllPersons.TryGetValue(person, out var per))
                                {
                                    return;
                                }

                                if (per.Name == message.Sender)
                                {
                                    _gameActions.SendMessageToWithArgs(message.Sender, Messages.Replic, ReplicCodes.Special.ToString(), LO[nameof(R.CannotKickYouself)]);
                                    return;
                                }

                                if (!per.IsHuman)
                                {
                                    _gameActions.SendMessageToWithArgs(message.Sender, Messages.Replic, ReplicCodes.Special.ToString(), LO[nameof(R.CannotKickBots)]);
                                    return;
                                }

                                await MasterServer.KickAsync(person);
                                _gameActions.SpecialReplic(string.Format(LO[nameof(R.Kicked)], message.Sender, person));
                                OnDisconnectRequested(person);
                            }
                            break;

                        case Messages.Ban:
                            if (message.Sender == ClientData.HostName & args.Length > 1)
                            {
                                var person = args[1];

                                if (!ClientData.AllPersons.TryGetValue(person, out var per))
                                {
                                    return;
                                }

                                if (per.Name == message.Sender)
                                {
                                    _gameActions.SendMessageToWithArgs(message.Sender, Messages.Replic, ReplicCodes.Special.ToString(), LO[nameof(R.CannotBanYourself)]);
                                    return;
                                }

                                if (!per.IsHuman)
                                {
                                    _gameActions.SendMessageToWithArgs(message.Sender, Messages.Replic, ReplicCodes.Special.ToString(), LO[nameof(R.CannotBanBots)]);
                                    return;
                                }

                                await MasterServer.KickAsync(person, true);
                                _gameActions.SpecialReplic(string.Format(LO[nameof(R.Banned)], message.Sender, person));
                                OnDisconnectRequested(person);
                            }
                            break;

                        case Messages.Mark:
                            if (!ClientData.CanMarkQuestion)
                            {
                                break;
                            }

                            ClientData.GameResultInfo.MarkedQuestions.Add(new AnswerInfo
                            {
                                Round = _logic.Engine.RoundIndex,
                                Theme = _logic.Engine.ThemeIndex,
                                Question = _logic.Engine.QuestionIndex,
                                Answer = ""
                            });
                            break;
                    }
                }
                catch (Exception exc)
                {
                    Share_Error(new Exception(message.Text, exc));
                }
            }, 5000);

        private void OnNextDelete(Message message, string[] args)
        {
            if (ClientData.IsWaiting &&
                ClientData.Decision == DecisionType.NextPersonFinalThemeDeleting &&
                message.Sender == ClientData.ShowMan.Name &&
                args.Length > 1 &&
                int.TryParse(args[1], out int playerIndex) &&
                playerIndex > -1 &&
                playerIndex < ClientData.Players.Count &&
                ClientData.Players[playerIndex].Flag)
            {
                ClientData.ThemeDeleters.Current.SetIndex(playerIndex);
                _logic.Stop(StopReason.Decision);
            }
        }

        private void OnPicture(Message message, string[] args)
        {
            var path = args[1];
            var person = ClientData.MainPersons.FirstOrDefault(item => message.Sender == item.Name);

            if (person == null)
            {
                return;
            }

            if (args.Length > 2)
            {
                var file = $"{message.Sender}_{Path.GetFileName(path)}";
                string uri;

                if (!ClientData.Share.ContainsUri(file))
                {
                    var base64image = args[2];

                    var imageDataSize = ((base64image.Length * 3) + 3) / 4 -
                        (base64image.Length > 0 && base64image[^1] == '=' ?
                            base64image.Length > 1 && base64image[^2] == '=' ?
                                2 : 1 : 0);

                    if (imageDataSize > MaxAvatarSize)
                    {
                        _gameActions.SendMessageToWithArgs(message.Sender, Messages.Replic, ReplicCodes.Special.ToString(), LO[nameof(R.AvatarTooBig)]);
                        return;
                    }

                    var imageData = new byte[imageDataSize]; // TODO: save to file system. Disable for game server

                    if (!Convert.TryFromBase64String(args[2], imageData, out var bytesWritten))
                    {
                        _gameActions.SendMessageToWithArgs(message.Sender, Messages.Replic, ReplicCodes.Special.ToString(), LO[nameof(R.InvalidAvatarData)]);
                        return;
                    }
                    
                    Array.Resize(ref imageData, bytesWritten);

                    uri = ClientData.Share.CreateUri(file, imageData, null);
                }
                else
                {
                    uri = ClientData.Share.MakeUri(file, null);
                }

                person.Picture = $"URI: {uri}";
            }
            else
            {
                person.Picture = path;
            }

            InformPicture(person);
        }

        private void OnStake(Message message, string[] args)
        {
            if (!ClientData.IsWaiting ||
                ClientData.Decision != DecisionType.AuctionStakeMaking ||
                (ClientData.ActivePlayer == null || message.Sender != ClientData.ActivePlayer.Name)
                && (!ClientData.IsOralNow || message.Sender != ClientData.ShowMan.Name))
            {
                return;
            }

            if (!int.TryParse(args[1], out var stakeType) || stakeType < 0 || stakeType > 3)
            {
                return;
            }

            ClientData.StakeType = (StakeMode)stakeType;

            if (!ClientData.StakeVariants[(int)ClientData.StakeType])
            {
                ClientData.StakeType = null;
            }
            else if (ClientData.StakeType == StakeMode.Sum)
            {
                var minimum = ClientData.Stake != -1 ? ClientData.Stake + 100 : ClientData.CurPriceRight + 100;

                // TODO: optimize
                while (minimum % 100 != 0)
                {
                    minimum++;
                }

                if (!int.TryParse(args[2], out var stakeSum))
                {
                    ClientData.StakeType = null;
                    return;
                }

                if (stakeSum < minimum || stakeSum > ClientData.ActivePlayer.Sum || stakeSum % 100 != 0)
                {
                    ClientData.StakeType = null;
                    return;
                }

                ClientData.StakeSum = stakeSum;
            }

            if (ClientData.IsOralNow)
            {
                _gameActions.SendMessage(
                    Messages.Cancel,
                    message.Sender == ClientData.ShowMan.Name ? ClientData.ActivePlayer.Name : ClientData.ShowMan.Name);
            }

            _logic.Stop(StopReason.Decision);
        }

        private void OnInfo(Message message)
        {
            Inform(message.Sender);

            foreach (var item in ClientData.MainPersons)
            {
                if (item.Ready)
                {
                    _gameActions.SendMessage($"{Messages.Ready}\n{item.Name}", message.Sender);
                }
            }

            _gameActions.InformStage(message.Sender);
            _gameActions.InformSums(message.Sender);

            if (ClientData.Stage != GameStage.Before)
            {
                _gameActions.InformRoundsNames(message.Sender);
            }

            if (ClientData.Stage == GameStage.Round)
            {
                ClientData.TableInformStageLock.WithLock(() =>
                {
                    if (ClientData.TableInformStage > 0)
                    {
                        _gameActions.InformRoundThemes(message.Sender, false);

                        if (ClientData.TableInformStage > 1)
                        {
                            _gameActions.InformTable(message.Sender);
                        }
                    }
                },
                5000);

                _gameActions.InformRoundContent(message.Sender);
            }
            else if (ClientData.Stage == GameStage.Before && ClientData.Settings.IsAutomatic)
            {
                var leftTimeBeforeStart = Constants.AutomaticGameStartDuration - (int)(DateTime.UtcNow - ClientData.TimerStartTime[2]).TotalSeconds * 10;

                if (leftTimeBeforeStart > 0)
                {
                    _gameActions.SendMessage(string.Join(Message.ArgsSeparator, Messages.Timer, 2, MessageParams.Timer_Go, leftTimeBeforeStart, -2), message.Sender);
                }
            }
        }

        private void OnDelete(Message message, string[] args)
        {
            if (!ClientData.IsWaiting ||
                ClientData.Decision != DecisionType.FinalThemeDeleting ||
                ClientData.ActivePlayer == null ||
                message.Sender != ClientData.ActivePlayer.Name && (!ClientData.IsOralNow || message.Sender != ClientData.ShowMan.Name))
            {
                return;
            }

            if (!int.TryParse(args[1], out int themeIndex) || themeIndex <= -1 || themeIndex >= ClientData.TInfo.RoundInfo.Count)
            {
                return;
            }

            if (ClientData.TInfo.RoundInfo[themeIndex].Name == QuestionHelper.InvalidThemeName)
            {
                return;
            }

            ClientData.ThemeIndexToDelete = themeIndex;

            if (ClientData.IsOralNow)
            {
                _gameActions.SendMessage(
                    Messages.Cancel,
                    message.Sender == ClientData.ShowMan.Name
                        ? ClientData.ActivePlayer.Name
                        : ClientData.ShowMan.Name);
            }

            _logic.Stop(StopReason.Decision);
        }

        private void OnDisconnect(string[] args)
        {
            if (args.Length < 3 || !ClientData.AllPersons.TryGetValue(args[1], out var account))
            {
                return;
            }

            var withError = args[2] == "+";

            var res = new StringBuilder()
                .Append(LO[account.IsMale ? nameof(R.Disconnected_Male) : nameof(R.Disconnected_Female)])
                .Append(' ')
                .Append(account.Name);

            _gameActions.SpecialReplic(res.ToString());
            _gameActions.SendMessageWithArgs(Messages.Disconnected, account.Name);

            ClientData.BeginUpdatePersons($"Disconnected {account.Name}");

            try
            {
                account.IsConnected = false;

                if (ClientData.Viewers.Contains(account))
                {
                    ClientData.Viewers.Remove(account);
                }
                else
                {
                    var isBefore = ClientData.Stage == GameStage.Before;

                    if (account is GamePersonAccount person)
                    {
                        person.Name = Constants.FreePlace;
                        person.Picture = "";

                        if (isBefore)
                        {
                            person.Ready = false;
                        }
                    }
                }
            }
            finally
            {
                ClientData.EndUpdatePersons();
            }

            if (args[1] == ClientData.HostName)
            {
                // A new host must be assigned if possible.
                // The host is assigned randomly

                SelectNewHost();

                if (ClientData.Settings.AppSettings.Managed && !_logic.IsRunning)
                {
                    if (_logic.StopReason == StopReason.Pause || ClientData.TInfo.Pause)
                    {
                        _logic.AddHistory($"Managed game pause autoremoved.");
                        OnPauseCore(false);
                        return;
                    }

                    _logic.AddHistory($"Managed game move autostarted.");

                    ClientData.MoveDirection = MoveDirections.Next;
                    _logic.Stop(StopReason.Move);
                }
            }

            OnPersonsChanged(false, withError);
        }

        private async ValueTask OnConnectAsync(Message message, string[] args)
        {
            if (args.Length < 4)
            {
                _gameActions.SendMessage(SystemMessages.Refuse + Message.ArgsSeparatorChar + LO[nameof(R.WrongConnectionParameters)], message.Sender);
                return;
            }

            if (!string.IsNullOrEmpty(ClientData.Settings.NetworkGamePassword) && (args.Length < 6 || ClientData.Settings.NetworkGamePassword != args[5]))
            {
                _gameActions.SendMessage(SystemMessages.Refuse + Message.ArgsSeparatorChar + LO[nameof(R.WrongPassword)], message.Sender);
                return;
            }

            var role = args[1];
            var name = args[2];
            var sex = args[3];

            if (ClientData.AllPersons.ContainsKey(name))
            {
                _gameActions.SendMessage(SystemMessages.Refuse + Message.ArgsSeparatorChar + string.Format(LO[nameof(R.PersonWithSuchNameIsAlreadyInGame)], name), message.Sender);
                return;
            }

            var index = -1;
            IEnumerable<ViewerAccount> accountsToSearch;

            switch (role)
            {
                case Constants.Showman:
                    accountsToSearch = new ViewerAccount[1] { ClientData.ShowMan };
                    break;

                case Constants.Player:
                    accountsToSearch = ClientData.Players;

                    if (ClientData.HostName == name) // Подключение организатора
                    {
                        var defaultPlayers = ClientData.Settings.Players;
                        for (var i = 0; i < defaultPlayers.Length; i++)
                        {
                            if (defaultPlayers[i].Name == name)
                            {
                                index = i;
                                break;
                            }
                        }

                        if (index < 0 || index >= ClientData.Players.Count)
                        {
                            _gameActions.SendMessage(string.Join(Message.ArgsSeparator, SystemMessages.Refuse, LO[nameof(R.PositionNotFoundByIndex)]), message.Sender);
                            return;
                        }
                    }

                    break;

                default:
                    accountsToSearch = ClientData.Viewers.Concat(new ViewerAccount[] { new ViewerAccount(Constants.FreePlace, false, false) { IsHuman = true } });
                    break;
            }

            var found = false;

            if (index > -1)
            {
                var accounts = accountsToSearch.ToArray();

                var (result, foundLocal) = await CheckAccountAsync(message, role, name, sex, index, accounts[index]);
                if (result.HasValue)
                {
                    if (!result.Value)
                    {
                        return;
                    }
                }

                found |= foundLocal;
            }
            else
            {
                foreach (var item in accountsToSearch)
                {
                    index++;
                    var (result, foundLocal) = await CheckAccountAsync(message, role, name, sex, index, item);

                    found |= foundLocal;

                    if (result.HasValue)
                    {
                        if (!result.Value)
                        {
                            return;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            if (!found)
            {
                _gameActions.SendMessage($"{SystemMessages.Refuse}{Message.ArgsSeparatorChar}{LO[nameof(R.NoFreePlaceForName)]}", message.Sender);
            }
        }

        private void SelectNewHost()
        {
            static bool canBeHost(ViewerAccount account) => account.IsHuman && account.IsConnected;

            string newHostName = null;

            if (canBeHost(ClientData.ShowMan))
            {
                newHostName = ClientData.ShowMan.Name;
            }
            else
            {
                var availablePlayers = ClientData.Players.Where(canBeHost).ToArray();

                if (availablePlayers.Length > 0)
                {
                    var index = ClientData.Rand.Next(availablePlayers.Length);
                    newHostName = availablePlayers[index].Name;
                }
                else
                {
                    var availableViewers = ClientData.Viewers.Where(canBeHost).ToArray();

                    if (availableViewers.Length > 0)
                    {
                        var index = ClientData.Rand.Next(availableViewers.Length);
                        newHostName = availableViewers[index].Name;
                    }
                }
            }

            UpdateHostName(newHostName);
        }

        private void UpdateHostName(string newHostName)
        {
            ClientData.HostName = newHostName;

            _gameActions.SendMessageWithArgs(Messages.Hostname, newHostName ?? "", "" /* by game */);
        }

        private void OnApellation(Message message, string[] args)
        {
            if (!ClientData.AllowAppellation)
            {
                return;
            }

            ClientData.IsAppelationForRightAnswer = args.Length == 1 || args[1] == "+";
            ClientData.AppellationSource = message.Sender;

            ClientData.AppelaerIndex = -1;

            if (ClientData.IsAppelationForRightAnswer)
            {
                for (var i = 0; i < ClientData.Players.Count; i++)
                {
                    if (ClientData.Players[i].Name == message.Sender)
                    {
                        for (var j = 0; j < ClientData.QuestionHistory.Count; j++)
                        {
                            var index = ClientData.QuestionHistory[j].PlayerIndex;

                            if (index == i)
                            {
                                if (!ClientData.QuestionHistory[j].IsRight)
                                {
                                    ClientData.AppelaerIndex = index;
                                }

                                break;
                            }
                        }

                        break;
                    }
                }
            }
            else
            {
                if (!ClientData.Players.Any(player => player.Name == message.Sender))
                {
                    // Апеллировать могут только игроки
                    return;
                }

                // Утверждение, что ответ неверен
                var count = ClientData.QuestionHistory.Count;

                if (count > 0 && ClientData.QuestionHistory[count - 1].IsRight)
                {
                    ClientData.AppelaerIndex = ClientData.QuestionHistory[count - 1].PlayerIndex;
                }
            }

            if (ClientData.AppelaerIndex != -1)
            {
                // Начата процедура апелляции
                ClientData.AllowAppellation = false;
                _logic.Stop(StopReason.Appellation);
            }
        }

        private void OnCatCost(Message message, string[] args)
        {
            if (!ClientData.IsWaiting ||
                ClientData.Decision != DecisionType.CatCostSetting ||
                (ClientData.Answerer == null || message.Sender != ClientData.Answerer.Name) &&
                (!ClientData.IsOralNow || message.Sender != ClientData.ShowMan.Name))
            {
                return;
            }

            if (int.TryParse(args[1], out int sum)
                && sum >= ClientData.CatInfo.Minimum
                && sum <= ClientData.CatInfo.Maximum
                && (sum - ClientData.CatInfo.Minimum) % ClientData.CatInfo.Step == 0)
            {
                ClientData.CurPriceRight = sum;
            }

            _logic.Stop(StopReason.Decision);
        }

        private void OnChanged(Message message, string[] args)
        {
            if (message.Sender != ClientData.ShowMan.Name || args.Length != 3)
            {
                return;
            }

            if (!int.TryParse(args[1], out var playerIndex) ||
                !int.TryParse(args[2], out var sum) ||
                playerIndex < 1 ||
                playerIndex > ClientData.Players.Count)
            {
                return;
            }

            var player = ClientData.Players[playerIndex - 1];
            player.Sum = sum;

            _gameActions.SpecialReplic($"{ClientData.ShowMan.Name} {LO[nameof(R.Change1)]} {player.Name}{LO[nameof(R.Change3)]} {Notion.FormatNumber(player.Sum)}");
            _gameActions.InformSums();

            _logic.AddHistory($"Sum change: {playerIndex - 1} = {sum}");
        }

        private void OnMove(Message message, string[] args)
        {
            if (message.Sender != ClientData.HostName && message.Sender != ClientData.ShowMan.Name || args.Length <= 1)
            {
                return;
            }

            if (!int.TryParse(args[1], out int direction))
            {
                return;
            }

            var moveDirection = (MoveDirections)direction;

            if (moveDirection < MoveDirections.RoundBack || moveDirection > MoveDirections.Round)
            {
                return;
            }

            switch (moveDirection)
            {
                case MoveDirections.RoundBack:
                    if (!_logic.Engine.CanMoveBackRound)
                    {
                        return;
                    }

                    break;

                case MoveDirections.Back:
                    if (!_logic.Engine.CanMoveBack)
                    {
                        return;
                    }

                    break;

                case MoveDirections.Next:
                    if (ClientData.MoveNextBlocked)
                    {
                        return;
                    }

                    break;

                case MoveDirections.RoundNext:
                    if (!_logic.Engine.CanMoveNextRound)
                    {
                        return;
                    }

                    break;

                case MoveDirections.Round:
                    if (!_logic.Engine.CanMoveNextRound && !_logic.Engine.CanMoveBackRound ||
                        ClientData.Package == null ||
                        args.Length <= 2 ||
                        !int.TryParse(args[2], out int roundIndex) ||
                        roundIndex < 0 ||
                        roundIndex >= ClientData.Rounds.Length ||
                        ClientData.Rounds[roundIndex].Index == _logic.Engine.RoundIndex)
                    {
                        return;
                    }

                    ClientData.TargetRoundIndex = ClientData.Rounds[roundIndex].Index;
                    break;
            }

            // Resume paused game
            if (ClientData.TInfo.Pause)
            {
                OnPauseCore(false);
                return;
            }

            _logic.AddHistory($"Move started: {ClientData.MoveDirection}");

            ClientData.MoveDirection = moveDirection;
            _logic.Stop(StopReason.Move);
        }

        private void OnReady(Message message, string[] args)
        {
            if (ClientData.Stage != GameStage.Before)
            {
                return;
            }

            var res = new StringBuilder();

            // Player or showman is ready to start the game
            res.Append(Messages.Ready).Append(Message.ArgsSeparatorChar);

            var readyAll = true;
            var found = false;
            var toReady = args.Length == 1 || args[1] == "+";

            foreach (var item in ClientData.MainPersons)
            {
                if (message.Sender == item.Name && (toReady && !item.Ready || !toReady && item.Ready))
                {
                    item.Ready = toReady;
                    res.Append(message.Sender).Append(Message.ArgsSeparatorChar).Append(toReady ? "+" : "-");
                    found = true;
                }

                readyAll = readyAll && item.Ready;
            }

            if (found)
            {
                _gameActions.SendMessage(res.ToString());
            }

            if (readyAll)
            {
                StartGame();
            }
            else if (ClientData.Settings.IsAutomatic)
            {
                if (ClientData.Players.All(player => player.IsConnected))
                {
                    StartGame();
                }
            }
        }

        private void OnPause(Message message, string[] args)
        {
            if (message.Sender != ClientData.HostName && message.Sender != ClientData.ShowMan.Name || args.Length <= 1)
            {
                return;
            }

            OnPauseCore(args[1] == "+");
        }

        private void OnPauseCore(bool isPauseEnabled)
        {
            // Game host or showman requested a game pause

            if (isPauseEnabled)
            {
                if (ClientData.TInfo.Pause)
                {
                    return;
                }

                if (_logic.Stop(StopReason.Pause))
                {
                    ClientData.TInfo.Pause = true;
                    Logic.AddHistory("Pause activated");
                }

                return;
            }

            if (_logic.StopReason == StopReason.Pause)
            {
                // We are currently moving into pause mode. Resuming
                ClientData.TInfo.Pause = false;
                _logic.AddHistory("Immediate pause resume");
                _logic.CancelStop();
                return;
            }

            if (!ClientData.TInfo.Pause)
            {
                return;
            }

            ClientData.TInfo.Pause = false;

            var pauseDuration = DateTime.UtcNow.Subtract(ClientData.PauseStartTime);

            var times = new int[Constants.TimersCount];

            for (var i = 0; i < Constants.TimersCount; i++)
            {
                times[i] = (int)(ClientData.PauseStartTime.Subtract(ClientData.TimerStartTime[i]).TotalMilliseconds / 100);
                ClientData.TimerStartTime[i] = ClientData.TimerStartTime[i].Add(pauseDuration);
            }

            if (ClientData.IsPlayingMediaPaused)
            {
                ClientData.IsPlayingMediaPaused = false;
                ClientData.IsPlayingMedia = true;
            }

            if (ClientData.IsThinkingPaused)
            {
                ClientData.IsThinkingPaused = false;
                ClientData.IsThinking = true;
            }

            _logic.AddHistory($"Pause resumed ({_logic.PrintOldTasks()} {_logic.StopReason})");

            try
            {
                var maxPressingTime = ClientData.Settings.AppSettings.TimeSettings.TimeForThinkingOnQuestion * 10;
                times[1] = maxPressingTime - _logic.ResumeExecution();
            }
            catch (Exception exc)
            {
                throw new Exception($"Resume execution error: {_logic.PrintHistory()}", exc);
            }

            if (_logic.StopReason == StopReason.Decision)
            {
                _logic.ExecuteImmediate(); // Decision could be ready
            }

            _gameActions.SpecialReplic(LO[nameof(R.GameResumed)]);
            _gameActions.SendMessageWithArgs(Messages.Pause, isPauseEnabled ? '+' : '-', times[0], times[1], times[2]);
        }

        private void OnAtom()
        {
            if (!ClientData.IsPlayingMedia || ClientData.TInfo.Pause)
            {
                return;
            }

            ClientData.HaveViewedAtom--;

            if (ClientData.HaveViewedAtom <= 0)
            {
                ClientData.IsPlayingMedia = false;

                _logic.ExecuteImmediate();
            }
            else
            {
                // Иногда кто-то отваливается, и процесс затягивается на 60 секунд. Это недопустимо. Дадим 3 секунды
                _logic.ScheduleExecution(Tasks.MoveNext, 30 + ClientData.Settings.AppSettings.TimeSettings.TimeForMediaDelay * 10, force: true);
            }
        }

        private void OnAnswer(Message message, string[] args)
        {
            if (ClientData.Decision != DecisionType.Answering)
            {
                return;
            }

            if (ClientData.Round != null && ClientData.Round.Type == RoundTypes.Final)
            {
                ClientData.AnswererIndex = -1;

                for (var i = 0; i < ClientData.Players.Count; i++)
                {
                    if (ClientData.Players[i].Name == message.Sender && ClientData.Players[i].InGame)
                    {
                        ClientData.AnswererIndex = i;

                        _gameActions.SendMessageWithArgs(Messages.PersonFinalAnswer, i);
                        break;
                    }
                }

                if (ClientData.AnswererIndex == -1)
                {
                    return;
                }
            }
            else if (!ClientData.IsWaiting || ClientData.Answerer != null && ClientData.Answerer.Name != message.Sender)
            {
                return;
            }

            if (ClientData.Answerer == null)
            {
                return;
            }

            if (!ClientData.Answerer.IsHuman)
            {
                if (args[1] == MessageParams.Answer_Right)
                {
                    ClientData.Answerer.Answer = args[2].Replace(Constants.AnswerPlaceholder, ClientData.Question.Right.FirstOrDefault() ?? "(...)");
                    ClientData.Answerer.AnswerIsWrong = false;
                }
                else
                {
                    ClientData.Answerer.AnswerIsWrong = true;

                    var restwrong = new List<string>();

                    foreach (var wrong in ClientData.Question.Wrong)
                    {
                        if (!ClientData.UsedWrongVersions.Contains(wrong))
                        {
                            restwrong.Add(wrong);
                        }
                    }

                    var wrongAnswers = LO[nameof(R.WrongAnswer)].Split(';');
                    var wrongCount = restwrong.Count;

                    if (wrongCount == 0)
                    {
                        for (int i = 0; i < wrongAnswers.Length; i++)
                        {
                            if (!ClientData.UsedWrongVersions.Contains(wrongAnswers[i]))
                            {
                                restwrong.Add(wrongAnswers[i]);
                            }
                        }

                        if (!ClientData.UsedWrongVersions.Contains(LO[nameof(R.NoAnswer)]))
                        {
                            restwrong.Add(LO[nameof(R.NoAnswer)]);
                        }
                    }

                    wrongCount = restwrong.Count;

                    if (wrongCount == 0)
                    {
                        restwrong.Add(wrongAnswers[0]);
                        wrongCount = 1;
                    }

                    int wrongIndex = ClientData.Rand.Next(wrongCount);

                    ClientData.UsedWrongVersions.Add(restwrong[wrongIndex]);
                    ClientData.Answerer.Answer = args[2].Replace("#", restwrong[wrongIndex]);
                }

                ClientData.Answerer.Answer = ClientData.Answerer.Answer.GrowFirstLetter();
            }
            else
            {
                if (args[1].Length > 0)
                {
                    ClientData.Answerer.Answer = args[1];
                    ClientData.Answerer.AnswerIsWrong = false;
                }
                else
                {
                    ClientData.Answerer.Answer = LO[nameof(R.IDontKnow)];
                    ClientData.Answerer.AnswerIsWrong = true;
                }
            }

            if (ClientData.Round.Type != RoundTypes.Final)
            {
                _logic.Stop(StopReason.Decision);
            }
        }

        private void OnIsRight(Message message, string[] args)
        {
            if (!ClientData.IsWaiting || args.Length <= 1)
            {
                return;
            }

            if (ClientData.ShowMan != null &&
                message.Sender == ClientData.ShowMan.Name &&
                ClientData.Answerer != null &&
                (ClientData.Decision == DecisionType.AnswerValidating || ClientData.IsOralNow && ClientData.Decision == DecisionType.Answering))
            {
                ClientData.Decision = DecisionType.AnswerValidating;
                ClientData.Answerer.AnswerIsRight = args[1] == "+";
                ClientData.ShowmanDecision = true;

                _logic.Stop(StopReason.Decision);
                return;
            }

            if (ClientData.Decision == DecisionType.AppellationDecision)
            {
                for (var i = 0; i < ClientData.Players.Count; i++)
                {
                    if (ClientData.Players[i].Flag && ClientData.Players[i].Name == message.Sender)
                    {
                        ClientData.AppellationAnswersRightReceivedCount += args[1] == "+" ? 1 : 0;
                        ClientData.Players[i].Flag = false;
                        ClientData.AppellationAnswersReceivedCount++;
                        _gameActions.SendMessageWithArgs(Messages.PersonApellated, i);
                    }
                }

                if (ClientData.AppellationAnswersReceivedCount == ClientData.Players.Count - 1)
                {
                    _logic.Stop(StopReason.Decision);
                }
            }
        }

        private void OnPass(Message message)
        {
            if (!ClientData.IsQuestionPlaying)
            {
                return;
            }

            var canPressChanged = false;

            for (var i = 0; i < ClientData.Players.Count; i++)
            {
                var player = ClientData.Players[i];

                if (player.Name == message.Sender && player.CanPress)
                {
                    player.CanPress = false;
                    _gameActions.SendMessageWithArgs(Messages.Pass, i);
                    canPressChanged = true;
                    break;
                }
            }

            if (canPressChanged && ClientData.Players.All(p => !p.CanPress) && !ClientData.TInfo.Pause)
            {
                if (!ClientData.IsAnswer)
                {
                    if (!ClientData.IsQuestionFinished)
                    {
                        _logic.Engine.MoveToAnswer();
                    }

                    _logic.ExecuteImmediate();
                }
            }
        }

        /// <summary>
        /// Handles player button press.
        /// </summary>
        /// <param name="playerName">Pressed player name.</param>
        private void OnI(string playerName)
        {
            if (ClientData.TInfo.Pause)
            {
                return;
            }

            if (ClientData.Decision != DecisionType.Pressing)
            {
                // Just show that the player has misfired the button
                HandlePlayerMisfire(playerName);
                return;
            }

            // Detect possible answerer
            var answererIndex = DetectAnswererIndex(playerName);

            if (answererIndex == -1)
            {
                return;
            }

            if (!ClientData.Settings.AppSettings.UsePingPenalty) // Default mode without penalties
            {
                ClientData.PendingAnswererIndex = answererIndex;

                if (_logic.Stop(StopReason.Answer))
                {
                    ClientData.Decision = DecisionType.None;
                }

                return;
            }

            // Special mode when answerer with penalty waits a little bit while other players with less penalty could try to press
            ProcessPenalizedAnswerer(answererIndex);
        }

        private void ProcessPenalizedAnswerer(int answererIndex)
        {
            var penalty = ClientData.Players[answererIndex].PingPenalty;
            var penaltyStartTime = DateTime.UtcNow;

            if (ClientData.IsDeferringAnswer)
            {
                var futureTime = penaltyStartTime.AddMilliseconds(penalty * 100);
                var currentFutureTime = ClientData.PenaltyStartTime.AddMilliseconds(ClientData.Penalty * 100);

                if (futureTime >= currentFutureTime) // New answerer candidate has bigger penalized time so he looses the hit
                {
                    return;
                }
            }

            ClientData.PendingAnswererIndex = answererIndex;

            if (penalty == 0) // Act like in mode without penalty
            {
                if (_logic.Stop(StopReason.Answer))
                {
                    ClientData.Decision = DecisionType.None;
                }
            }
            else
            {
                ClientData.PenaltyStartTime = penaltyStartTime;
                ClientData.Penalty = penalty;

                _logic.Stop(StopReason.Wait);
            }
        }

        private int DetectAnswererIndex(string playerName)
        {
            var answererIndex = -1;
            var blockingButtonTime = ClientData.Settings.AppSettings.TimeSettings.TimeForBlockingButton;

            for (var i = 0; i < ClientData.Players.Count; i++)
            {
                var player = ClientData.Players[i];

                if (player.Name == playerName &&
                    player.CanPress &&
                    DateTime.UtcNow.Subtract(player.LastBadTryTime).TotalSeconds >= blockingButtonTime)
                {
                    answererIndex = i;
                    break;
                }
            }

            return answererIndex;
        }

        private void HandlePlayerMisfire(string playerName)
        {
            for (var i = 0; i < ClientData.Players.Count; i++)
            {
                var player = ClientData.Players[i];

                if (player.Name == playerName)
                {
                    if (ClientData.Answerer != player)
                    {
                        player.LastBadTryTime = DateTime.UtcNow;
                        _gameActions.SendMessageWithArgs(Messages.WrongTry, i);
                    }

                    return;
                }
            }
        }

        private void OnDisconnectRequested(string person)
        {
            DisconnectRequested?.Invoke(person);
        }

        /// <summary>
        /// Изменить конфигурацию игры
        /// </summary>
        /// <param name="message"></param>
        /// <param name="args"></param>
        private void ProcessConfig(Message message, string[] args)
        {
            if (message.Sender != ClientData.HostName || args.Length <= 1)
            {
                return;
            }

            if (ClientData.HostName == null || !ClientData.AllPersons.TryGetValue(ClientData.HostName, out var host))
            {
                return;
            }

            switch (args[1])
            {
                case MessageParams.Config_AddTable:
                    AddTable(message, host);
                    break;

                case MessageParams.Config_DeleteTable:
                    DeleteTable(message, args, host);
                    break;

                case MessageParams.Config_Free:
                    FreeTable(message, args, host);
                    break;

                case MessageParams.Config_Set:
                    SetPerson(args, host);
                    break;

                case MessageParams.Config_ChangeType:
                    if (ClientData.Stage == GameStage.Before && args.Length > 2)
                    {
                        ChangePersonType(args[2], args.Length < 4 ? "" : args[3], host);
                    }
                    break;
            }
        }

        private void AddTable(Message message, Account host)
        {
            if (ClientData.Players.Count >= Constants.MaxPlayers)
            {
                return;
            }

            var newAccount = new ViewerAccount(Constants.FreePlace, false, false) { IsHuman = true };

            ClientData.BeginUpdatePersons("AddTable " + message.Text);

            try
            {
                ClientData.Players.Add(new GamePlayerAccount(newAccount));
                Logic.AddHistory($"Player added (total: {ClientData.Players.Count})");
            }
            finally
            {
                ClientData.EndUpdatePersons();
            }

            var info = new StringBuilder(Messages.Config).Append(Message.ArgsSeparatorChar)
                .Append(MessageParams.Config_AddTable).Append(Message.ArgsSeparatorChar);

            AppendAccountExt(newAccount, info);

            _gameActions.SendMessage(info.ToString());
            _gameActions.SpecialReplic($"{ClientData.HostName} {ResourceHelper.GetSexString(LO[nameof(R.Sex_Added)], host.IsMale)} {LO[nameof(R.NewGameTable)]}");
            OnPersonsChanged();
        }

        private void DeleteTable(Message message, string[] args, Account host)
        {
            if (args.Length <= 2)
            {
                return;
            }

            var indexStr = args[2];
            if (ClientData.Players.Count <= 2 || !int.TryParse(indexStr, out int index) || index <= -1
                || index >= ClientData.Players.Count)
            {
                return;
            }

            var account = ClientData.Players[index];
            var isOnline = account.IsConnected;

            if (ClientData.Stage != GameStage.Before && account.IsHuman && isOnline)
            {
                return;
            }

            ClientData.BeginUpdatePersons("DeleteTable " + message.Text);

            try
            {
                ClientData.Players.RemoveAt(index);
                Logic.AddHistory($"Player removed at {index}");

                DropPlayerIndex(index);

                if (isOnline && account.IsHuman)
                {
                    ClientData.Viewers.Add(account);
                }
            }
            finally
            {
                ClientData.EndUpdatePersons();
            }

            if (!account.IsHuman)
            {
                // Удалить клиента компьютерного игрока
                if (!_client.Server.DeleteClient(account.Name))
                {
                    _client.Server.OnError(new Exception($"Cannot delete client {account.Name}"), true);
                }
                else if (_client.Server.Contains(account.Name))
                {
                    _client.Server.OnError(new Exception($"Client {account.Name} was deleted but is still present on the server!"), true);
                }
            }

            _gameActions.SendMessageWithArgs(Messages.Config, MessageParams.Config_DeleteTable, index);
            _gameActions.SpecialReplic($"{ClientData.HostName} {ResourceHelper.GetSexString(LO[nameof(R.Sex_Deleted)], host.IsMale)} {LO[nameof(R.GameTableNumber)]} {index + 1}");

            if (ClientData.Stage == GameStage.Before)
            {
                var readyAll = ClientData.MainPersons.All(p => p.Ready);

                if (readyAll)
                {
                    StartGame();
                }
            }

            OnPersonsChanged();
        }

        private void PlanExecution(Tasks task, double taskTime, int arg = 0)
        {
            Logic.AddHistory($"PlanExecution {task} {taskTime} {arg} ({ClientData.TInfo.Pause})");

            if (ClientData.TInfo.Pause)
            {
                Logic.UpdatePausedTask((int)task, arg, (int)taskTime);
            }
            else
            {
                Logic.ScheduleExecution(task, taskTime, arg);
            }
        }

        private void DropPlayerIndex(int playerIndex)
        {
            if (ClientData.ChooserIndex > playerIndex)
            {
                ClientData.ChooserIndex--;
            }
            else if (ClientData.ChooserIndex == playerIndex)
            {
                // Передадим право выбора игроку с наименьшей суммой
                var minSum = ClientData.Players.Min(p => p.Sum);
                ClientData.ChooserIndex = ClientData.Players.TakeWhile(p => p.Sum != minSum).Count();
            }

            if (ClientData.AnswererIndex > playerIndex)
            {
                ClientData.AnswererIndex--;
            }
            else if (ClientData.AnswererIndex == playerIndex)
            {
                // Drop answerer index
                ClientData.AnswererIndex = -1;

                var nextTask = (Tasks)(ClientData.TInfo.Pause ? Logic.NextTask : Logic.CurrentTask);

                Logic.AddHistory(
                    $"AnswererIndex dropped; nextTask = {nextTask};" +
                    $" ClientData.Decision = {ClientData.Decision}; Logic.IsFinalRound() = {Logic.IsFinalRound()}");

                if ((ClientData.Decision == DecisionType.Answering ||
                    ClientData.Decision == DecisionType.AnswerValidating) && !Logic.IsFinalRound())
                {
                    // Answerer has been dropped. The game should be moved forward
                    Logic.StopWaiting();

                    if (ClientData.IsOralNow)
                    {
                        _gameActions.SendMessage(Messages.Cancel, ClientData.ShowMan.Name);
                    }

                    PlanExecution(Tasks.ContinueQuestion, 1);
                }
                else if (nextTask == Tasks.AskRight)
                {
                    // Игрока удалил после того, как он дал ответ. Но ещё не обратились к ведущему
                    PlanExecution(Tasks.ContinueQuestion, 1);
                }
                else if (nextTask == Tasks.CatInfo || nextTask == Tasks.AskCatCost || nextTask == Tasks.WaitCatCost)
                {
                    Logic.Engine.SkipQuestion();
                    PlanExecution(Tasks.MoveNext, 20, 1);
                }
                else if (nextTask == Tasks.AnnounceStake)
                {
                    PlanExecution(Tasks.Announce, 15);
                }
            }

            if (ClientData.AppelaerIndex > playerIndex)
            {
                ClientData.AppelaerIndex--;
            }
            else if (ClientData.AppelaerIndex == playerIndex)
            {
                ClientData.AppelaerIndex = -1;
                Logic.AddHistory($"AppelaerIndex dropped");
            }

            if (ClientData.StakerIndex > playerIndex)
            {
                ClientData.StakerIndex--;
            }
            else if (ClientData.StakerIndex == playerIndex)
            {
                var stakersCount = ClientData.Players.Count(p => p.StakeMaking);

                if (stakersCount == 1)
                {
                    for (int i = 0; i < ClientData.Players.Count; i++)
                    {
                        if (ClientData.Players[i].StakeMaking)
                        {
                            ClientData.StakerIndex = i;
                            Logic.AddHistory($"StakerIndex set to {i}");
                            break;
                        }
                    }
                }
                else
                {
                    ClientData.StakerIndex = -1;
                    Logic.AddHistory("StakerIndex dropped");
                }
            }

            var currentOrder = ClientData.Order;

            if (currentOrder != null && ClientData.Type != null && ClientData.Type.Name == QuestionTypes.Auction)
            {
                ClientData.OrderHistory
                    .Append("Before ")
                    .Append(playerIndex)
                    .Append(' ')
                    .Append(string.Join(",", currentOrder))
                    .AppendFormat(" {0}", ClientData.OrderIndex)
                    .AppendLine();

                var newOrder = new int[ClientData.Players.Count];

                for (int i = 0, j = 0; i < currentOrder.Length; i++)
                {
                    if (currentOrder[i] == playerIndex)
                    {
                        if (ClientData.OrderIndex >= i)
                        {
                            ClientData.OrderIndex--; // -1 - OK
                        }
                    }
                    else
                    {
                        newOrder[j++] = currentOrder[i] - (currentOrder[i] > playerIndex ? 1 : 0);

                        if (j == newOrder.Length)
                        {
                            break;
                        }
                    }
                }

                if (ClientData.OrderIndex == currentOrder.Length - 1)
                {
                    ClientData.OrderIndex = newOrder.Length - 1;
                }

                ClientData.Order = newOrder;

                ClientData.OrderHistory.Append("After ").Append(string.Join(",", newOrder)).AppendFormat(" {0}", ClientData.OrderIndex).AppendLine();

                if (!ClientData.Players.Any(p => p.StakeMaking))
                {
                    Logic.AddHistory($"Last staker dropped");
                    Logic.Engine.SkipQuestion();
                    PlanExecution(Tasks.MoveNext, 20, 1);
                }
                else if (ClientData.OrderIndex == -1 || ClientData.Order[ClientData.OrderIndex] == -1)
                {
                    Logic.AddHistory($"Current staker dropped");
                    if (ClientData.Decision == DecisionType.AuctionStakeMaking || ClientData.Decision == DecisionType.NextPersonStakeMaking)
                    {
                        // Ставящего удалили. Нужно продвинуть игру дальше
                        Logic.StopWaiting();

                        if (ClientData.IsOralNow || ClientData.Decision == DecisionType.NextPersonStakeMaking)
                        {
                            _gameActions.SendMessage(Messages.Cancel, ClientData.ShowMan.Name);
                        }

                        ContinueMakingStakes();
                    }
                }
                else if (ClientData.Decision == DecisionType.NextPersonStakeMaking)
                {
                    Logic.StopWaiting();
                    _gameActions.SendMessage(Messages.Cancel, ClientData.ShowMan.Name);

                    ContinueMakingStakes();
                }
            }

            if (Logic.IsFinalRound())
            {
                bool noPlayersLeft;

                if (ClientData.ThemeDeleters != null)
                {
                    ClientData.ThemeDeleters.RemoveAt(playerIndex);
                    noPlayersLeft = ClientData.ThemeDeleters.IsEmpty();
                }
                else
                {
                    noPlayersLeft = ClientData.Players.All(p => !p.InGame);
                }

                if (noPlayersLeft)
                {
                    ClientData.Decision = DecisionType.None;
                    
                    // All players that could play are removed
                    if (Logic.Engine.CanMoveNextRound)
                    {
                        Logic.Engine.MoveNextRound(); // Finishing current round
                    }
                    else
                    {
                        // TODO: it is better to provide a correct command to the game engine
                        PlanExecution(Tasks.Winner, 10); // This is the last round. Finishing game
                    }
                }
                else if (ClientData.Decision == DecisionType.NextPersonFinalThemeDeleting)
                {
                    var indicies = ClientData.ThemeDeleters.Current.PossibleIndicies;

                    for (var i = 0; i < ClientData.Players.Count; i++)
                    {
                        ClientData.Players[i].Flag = indicies.Contains(i);
                    }
                }
            }

            var newHistory = new List<AnswerResult>();

            for (var i = 0; i < ClientData.QuestionHistory.Count; i++)
            {
                var answerResult = ClientData.QuestionHistory[i];

                if (answerResult.PlayerIndex == playerIndex)
                {
                    continue;
                }

                newHistory.Add(
                    new AnswerResult
                    {
                        IsRight = answerResult.IsRight,
                        PlayerIndex = answerResult.PlayerIndex - (answerResult.PlayerIndex > playerIndex ? 1 : 0)
                    });
            }

            ClientData.QuestionHistory.Clear();
            ClientData.QuestionHistory.AddRange(newHistory);

            if (!ClientData.IsWaiting)
            {
                return;
            }

            switch (ClientData.Decision)
            {
                case DecisionType.StarterChoosing:
                    // Asking again
                    _gameActions.SendMessage(Messages.Cancel, ClientData.ShowMan.Name);
                    _logic.StopWaiting();
                    PlanExecution(Tasks.AskFirst, 20);
                    break;
            }
        }

        private void ContinueMakingStakes()
        {
            if (ClientData.Players.Count(p => p.StakeMaking) == 1)
            {
                for (var i = 0; i < ClientData.Players.Count; i++)
                {
                    if (ClientData.Players[i].StakeMaking)
                    {
                        ClientData.StakerIndex = i;
                    }
                }

                if (ClientData.Stake == -1)
                {
                    ClientData.Stake = ClientData.CurPriceRight;
                }

                PlanExecution(Tasks.PrintAuctPlayer, 10);
            }
            else
            {
                PlanExecution(Tasks.AskStake, 20);
            }
        }

        private void FreeTable(Message message, string[] args, Account host)
        {
            if (ClientData.Stage != GameStage.Before || args.Length <= 2)
            {
                return;
            }

            var personType = args[2];

            GamePersonAccount account;
            int index = -1;
            var isPlayer = personType == Constants.Player;

            if (isPlayer)
            {
                if (args.Length < 4)
                {
                    return;
                }

                var indexStr = args[3];

                if (!int.TryParse(indexStr, out index) || index < 0 || index >= ClientData.Players.Count)
                {
                    return;
                }

                account = ClientData.Players[index];
            }
            else
            {
                account = ClientData.ShowMan;
            }

            if (!account.IsConnected || !account.IsHuman)
            {
                return;
            }

            var newAccount = new Account { IsHuman = true, Name = Constants.FreePlace };

            ClientData.BeginUpdatePersons("FreeTable " + message.Text);

            try
            {
                if (isPlayer)
                {
                    ClientData.Players[index] = new GamePlayerAccount(newAccount);
                }
                else
                {
                    ClientData.ShowMan = new GamePersonAccount(newAccount);
                }

                account.Ready = false;

                ClientData.Viewers.Add(account);
            }
            finally
            {
                ClientData.EndUpdatePersons();
            }

            foreach (var item in ClientData.MainPersons)
            {
                if (item.Ready)
                {
                    _gameActions.SendMessage($"{Messages.Ready}\n{item.Name}", message.Sender);
                }
            }

            _gameActions.SendMessageWithArgs(Messages.Config, MessageParams.Config_Free, args[2], args[3]);
            _gameActions.SpecialReplic($"{ClientData.HostName} {ResourceHelper.GetSexString(LO[nameof(R.Sex_Free)], host.IsMale)} {account.Name} {LO[nameof(R.FromTable)]}");

            OnPersonsChanged();
        }

        private void SetPerson(string[] args, Account host)
        {
            if (ClientData.Stage != GameStage.Before || args.Length <= 4)
            {
                return;
            }

            var personType = args[2];
            var replacer = args[4];

            // Кого заменяем
            GamePersonAccount account;
            int index = -1;

            var isPlayer = personType == Constants.Player;

            if (isPlayer)
            {
                var indexStr = args[3];

                if (!int.TryParse(indexStr, out index) || index < 0 || index >= ClientData.Players.Count)
                {
                    return;
                }

                account = ClientData.Players[index];
            }
            else
            {
                account = ClientData.ShowMan;
            }

            var oldName = account.Name;
            GamePersonAccount newAccount;

            if (!account.IsHuman)
            {
                if (ClientData.AllPersons.ContainsKey(replacer))
                {
                    _gameActions.SpecialReplic(string.Format(LO[nameof(R.PersonAlreadyExists)], replacer));
                    return;
                }

                ClientData.BeginUpdatePersons($"SetComputerPerson {account.Name} {account.IsConnected} {replacer} {index}");

                try
                {
                    newAccount = isPlayer
                        ? ReplaceComputerPlayer(index, account.Name, replacer)
                        : ReplaceComputerShowman(account.Name, replacer);
                }
                finally
                {
                    ClientData.EndUpdatePersons();
                }

                if (newAccount == null)
                {
                    return;
                }
            }
            else
            {
                SetHumanPerson(isPlayer, account, replacer, index);
                newAccount = account;
            }

            _gameActions.SendMessageWithArgs(Messages.Config, MessageParams.Config_Set, args[2], args[3], args[4], account.IsMale ? '+' : '-');
            _gameActions.SpecialReplic($"{ClientData.HostName} {ResourceHelper.GetSexString(LO[nameof(R.Sex_Replaced)], host.IsMale)} {oldName} {LO[nameof(R.To)]} {replacer}");

            InformPicture(newAccount);
            OnPersonsChanged();
        }

        internal GamePersonAccount ReplaceComputerShowman(string oldName, string replacer)
        {
            for (var j = 0; j < _defaultShowmans.Length; j++)
            {
                if (_defaultShowmans[j].Name == replacer)
                {
                    _client.Server.DeleteClient(oldName);

                    return CreateNewComputerShowman(_defaultShowmans[j]);
                }
            }

            _client.Server.OnError(new Exception($"Default showman with name {replacer} not found"), true);
            return null;
        }

        internal GamePlayerAccount ReplaceComputerPlayer(int index, string oldName, string replacer)
        {
            for (var j = 0; j < _defaultPlayers.Length; j++)
            {
                if (_defaultPlayers[j].Name == replacer)
                {
                    _client.Server.DeleteClient(oldName);

                    return CreateNewComputerPlayer(index, _defaultPlayers[j]);
                }
            }

            _client.Server.OnError(new Exception($"Default player with name {replacer} not found"), true);
            return null;
        }

        internal void SetHumanPerson(bool isPlayer, GamePersonAccount account, string replacer, int index)
        {
            int otherIndex = -1;
            // На кого заменяем
            ViewerAccount otherAccount = null;

            ClientData.BeginUpdatePersons($"SetHumanPerson {account.Name} {account.IsConnected} {replacer} {index}");

            try
            {
                if (ClientData.ShowMan.Name == replacer && ClientData.ShowMan.IsHuman)
                {
                    otherAccount = ClientData.ShowMan;
                    ClientData.ShowMan = new GamePersonAccount(account)
                    {
                        Ready = account.Ready,
                        IsConnected = account.IsConnected
                    };
                }
                else
                {
                    for (var i = 0; i < ClientData.Players.Count; i++)
                    {
                        if (ClientData.Players[i].Name == replacer && ClientData.Players[i].IsHuman)
                        {
                            otherAccount = ClientData.Players[i];
                            ClientData.Players[i] = new GamePlayerAccount(account)
                            {
                                Ready = account.Ready,
                                IsConnected = account.IsConnected
                            };

                            otherIndex = i;
                            break;
                        }
                    }

                    if (otherIndex == -1)
                    {
                        for (var i = 0; i < ClientData.Viewers.Count; i++)
                        {
                            if (ClientData.Viewers[i].Name == replacer) // always IsHuman
                            {
                                otherAccount = ClientData.Viewers[i];
                                otherIndex = i;

                                if (account.IsConnected)
                                {
                                    ClientData.Viewers[i] = new ViewerAccount(account) { IsConnected = true };
                                }
                                else
                                {
                                    ClientData.Viewers.RemoveAt(i);
                                }

                                break;
                            }
                        }
                    }

                    if (otherIndex == -1)
                    {
                        return;
                    }
                }

                // Живой персонаж меняется на другого живого
                var otherPerson = otherAccount as GamePersonAccount;
                if (isPlayer)
                {
                    ClientData.Players[index] = new GamePlayerAccount(otherAccount) { IsConnected = otherAccount.IsConnected };

                    if (otherPerson != null)
                    {
                        ClientData.Players[index].Ready = otherPerson.Ready;
                    }
                }
                else
                {
                    ClientData.ShowMan = new GamePersonAccount(otherAccount) { IsConnected = otherAccount.IsConnected };

                    if (otherPerson != null)
                    {
                        ClientData.ShowMan.Ready = otherPerson.Ready;
                    }
                }

                InformPicture(otherAccount);
            }
            finally
            {
                ClientData.EndUpdatePersons();
            }
        }

        internal void ChangePersonType(string personType, string indexStr, ViewerAccount responsePerson)
        {
            GamePersonAccount account;
            int index = -1;

            var isPlayer = personType == Constants.Player;

            if (isPlayer)
            {
                if (!int.TryParse(indexStr, out index) || index < 0 || index >= ClientData.Players.Count)
                {
                    return;
                }

                account = ClientData.Players[index];
            }
            else
            {
                account = ClientData.ShowMan;
            }

            if (account == null)
            {
                ClientData.BackLink.LogWarning("ChangePersonType: account == null");
                return;
            }

            var oldName = account.Name;

            var newType = !account.IsHuman;
            string newName = "";
            bool newIsMale = true;

            Account newAcc = null;

            ClientData.BeginUpdatePersons($"ChangePersonType {personType} {indexStr}");

            try
            {
                if (account.IsConnected && account.IsHuman)
                {
                    ClientData.Viewers.Add(account);
                }

                if (!account.IsHuman)
                {
                    if (!_client.Server.DeleteClient(account.Name))
                    {
                        _client.Server.OnError(new Exception($"Cannot delete client {account.Name}"), true);
                    }
                    else if (_client.Server.Contains(account.Name))
                    {
                        _client.Server.OnError(new Exception($"Client {account.Name} was deleted but is still present on the server!"), true);
                    }

                    account.IsHuman = true;
                    newName = account.Name = Constants.FreePlace;
                    account.Picture = "";
                    account.Ready = false;
                    account.IsConnected = false;
                }
                else if (isPlayer)
                {
                    if (_defaultPlayers == null)
                    {
                        return;
                    }

                    var visited = new List<int>();

                    for (var i = 0; i < ClientData.Players.Count; i++)
                    {
                        if (i != index && !ClientData.Players[i].IsHuman)
                        {
                            for (var j = 0; j < _defaultPlayers.Length; j++)
                            {
                                if (_defaultPlayers[j].Name == ClientData.Players[i].Name)
                                {
                                    visited.Add(j);
                                    break;
                                }
                            }
                        }
                    }

                    var rand = ClientData.Rand.Next(_defaultPlayers.Length - visited.Count - 1);

                    while (visited.Contains(rand))
                    {
                        rand++;
                    }

                    var compPlayer = _defaultPlayers[rand];
                    newAcc = CreateNewComputerPlayer(index, compPlayer);
                    newName = newAcc.Name;
                    newIsMale = newAcc.IsMale;
                }
                else
                {
                    var showman = new ComputerAccount(_defaultShowmans[0]);
                    var name = showman.Name;
                    var nameIndex = 0;

                    while (nameIndex < Constants.MaxPlayers && ClientData.AllPersons.ContainsKey(name))
                    {
                        name = $"{showman.Name} {nameIndex++}";
                    }

                    showman.Name = name;

                    newAcc = CreateNewComputerShowman(showman);
                    newName = newAcc.Name;
                    newIsMale = newAcc.IsMale;
                }
            }
            finally
            {
                ClientData.EndUpdatePersons();
            }

            foreach (var item in ClientData.MainPersons)
            {
                if (item.Ready)
                {
                    _gameActions.SendMessage($"{Messages.Ready}\n{item.Name}");
                }
            }

            _gameActions.SendMessageWithArgs(Messages.Config, MessageParams.Config_ChangeType, personType, index, newType ? '+' : '-', newName, newIsMale ? '+' : '-');

            if (responsePerson != null)
            {
                var newTypeString = newType ? LO[nameof(R.Human)] : LO[nameof(R.Computer)];
                _gameActions.SpecialReplic($"{ClientData.HostName} {ResourceHelper.GetSexString(LO[nameof(R.Sex_Changed)], responsePerson.IsMale)} {LO[nameof(R.PersonType)]} {oldName} {LO[nameof(R.To)]} \"{newTypeString}\"");
            }

            if (newAcc != null)
            {
                InformPicture(newAcc);
            }

            OnPersonsChanged();
        }

        private GamePlayerAccount CreateNewComputerPlayer(int index, ComputerAccount account)
        {
            var newAccount = new GamePlayerAccount
            {
                IsHuman = false,
                Name = account.Name,
                IsMale = account.IsMale,
                Picture = account.Picture,
                IsConnected = true
            };

            ClientData.Players[index] = newAccount;

            var playerClient = new Client(newAccount.Name);
            _ = new Player(playerClient, account, false, LO, new ViewerData(ClientData.BackLink));

            playerClient.ConnectTo(_client.Server);
            Inform(newAccount.Name);

            return newAccount;
        }

        private GamePersonAccount CreateNewComputerShowman(ComputerAccount account)
        {
            if (ClientData.BackLink == null)
            {
                throw new InvalidOperationException($"{nameof(CreateNewComputerShowman)}: this.ClientData.BackLink == null");
            }

            var newAccount = new GamePersonAccount
            {
                IsHuman = false,
                Name = account.Name,
                IsMale = account.IsMale,
                Picture = account.Picture,
                IsConnected = true
            };

            ClientData.ShowMan = newAccount;

            var showmanClient = new Client(newAccount.Name);
            var showman = new Showman(showmanClient, account, false, LO, new ViewerData(ClientData.BackLink));

            showmanClient.ConnectTo(_client.Server);
            Inform(newAccount.Name);

            return newAccount;
        }

        internal void StartGame()
        {
            ClientData.Stage = GameStage.Begin;

            _logic.OnStageChanged(GameStages.Started, LO[nameof(R.GameBeginning)]);
            _gameActions.InformStage();

            ClientData.IsOral = ClientData.Settings.AppSettings.Oral && ClientData.ShowMan.IsHuman;

            _logic.ScheduleExecution(Tasks.StartGame, 1, 1);
        }

        private async Task<(bool? Result, bool Found)> CheckAccountAsync(
            Message message,
            string role,
            string name,
            string sex,
            int index,
            ViewerAccount account)
        {
            if (account.IsConnected)
            {
                return (null, false);
            }

            if (account.Name == name || account.Name == Constants.FreePlace)
            {
                var connectionFound = await _client.Server.ConnectionsLock.WithLockAsync(() =>
                {
                    var connection = MasterServer.Connections.Where(conn => conn.Id == message.Sender[1..]).FirstOrDefault();
                    
                    if (connection == null)
                    {
                        return false;
                    }

                    lock (connection.ClientsSync)
                    {
                        connection.Clients.Add(name);
                    }

                    connection.IsAuthenticated = true;
                    connection.UserName = name;

                    return true;
                });

                if (!connectionFound)
                {
                    return (false, true);
                }

                ClientData.BeginUpdatePersons($"Connected {name} as {role} as {index}");

                try
                {
                    var append = role == "viewer" && account.Name == Constants.FreePlace;
                    account.Name = name;
                    account.IsMale = sex == "m";
                    account.Picture = "";
                    account.IsConnected = true;

                    if (append)
                    {
                        ClientData.Viewers.Add(new ViewerAccount(account) { IsConnected = account.IsConnected });
                    }
                }
                finally
                {
                    ClientData.EndUpdatePersons();
                }

                _gameActions.SpecialReplic($"{LO[account.IsMale ? nameof(R.Connected_Male) : nameof(R.Connected_Female)]} {name}");

                _gameActions.SendMessage(Messages.Accepted, name);
                _gameActions.SendMessageWithArgs(Messages.Connected, role, index, name, sex, "");

                if (ClientData.HostName == null && !ClientData.Settings.IsAutomatic)
                {
                    UpdateHostName(name);
                }

                OnPersonsChanged();
            }

            return (true, true);
        }

        private bool? CheckAccountNew(
            string role,
            string name,
            string sex,
            ref bool found,
            int index,
            ViewerAccount account,
            Action connectionAuthenticator)
        {
            if (account.IsConnected)
            {
                return account.Name == name ? false : (bool?)null;
            }

            found = true;

            ClientData.BeginUpdatePersons($"Connected {name} as {role} as {index}");

            try
            {
                var append = role == "viewer" && account.Name == Constants.FreePlace;

                account.Name = name;
                account.IsMale = sex == "m";
                account.Picture = "";
                account.IsConnected = true;

                if (append)
                {
                    ClientData.Viewers.Add(new ViewerAccount(account) { IsConnected = account.IsConnected });
                }
            }
            finally
            {
                ClientData.EndUpdatePersons();
            }

            _gameActions.SpecialReplic($"{LO[account.IsMale ? nameof(R.Connected_Male) : nameof(R.Connected_Female)]} {name}");
            _gameActions.SendMessageWithArgs(Messages.Connected, role, index, name, sex, "");

            if (ClientData.HostName == null && !ClientData.Settings.IsAutomatic)
            {
                UpdateHostName(name);
            }

            connectionAuthenticator();

            OnPersonsChanged();

            return true;
        }

        private void OnPersonsChanged(bool joined = true, bool withError = false) => PersonsChanged?.Invoke(joined, withError);

        private void InformPicture(Account account)
        {
            foreach (var personName in ClientData.AllPersons.Keys)
            {
                if (account.Name != personName && personName != NetworkConstants.GameName)
                {
                    InformPicture(account, personName);
                }
            }
        }

        private void InformPicture(Account account, string receiver)
        {
            if (string.IsNullOrEmpty(account.Picture))
            {
                return;
            }

            var link = CreateUri(account.Name, account.Picture, receiver);

            if (link != null)
            {
                _gameActions.SendMessage(string.Join(Message.ArgsSeparator, Messages.Picture, account.Name, link), receiver);
            }
        }

        private string CreateUri(string id, string file, string receiver)
        {
            var local = _client.Server.Contains(receiver);

            if (!Uri.TryCreate(file, UriKind.RelativeOrAbsolute, out Uri uri))
            {
                return null;
            }

            if (!uri.IsAbsoluteUri || uri.Scheme == "file" && !CoreManager.Instance.FileExists(file))
            {
                return null;
            }

            var remote = !local && uri.Scheme == "file";
            var isURI = file.StartsWith("URI: ");

            if (isURI || remote)
            {
                string path = null;

                if (isURI)
                {
                    path = file[5..];
                }
                else
                {
                    var complexName = (id != null ? id + "_" : "") + Path.GetFileName(file);

                    if (!ClientData.Share.ContainsUri(complexName))
                    {
                        path = ClientData.Share.CreateUri(
                            complexName,
                            () =>
                            {
                                var stream = CoreManager.Instance.GetFile(file);
                                return new StreamInfo { Stream = stream, Length = stream.Length };
                            },
                            null);
                    }
                    else
                    {
                        path = ClientData.Share.MakeUri(complexName, null);
                    }
                }

                return local ? path : path.Replace("http://localhost", "http://" + Constants.GameHost);
            }
            else
            {
                return file;
            }
        }

        /// <summary>
        /// Начать игру даже при отсутствии участников (заполнив пустые слоты ботами)
        /// </summary>
        internal void AutoGame()
        {
            // Заполняем пустые слоты ботами
            for (var i = 0; i < ClientData.Players.Count; i++)
            {
                if (!ClientData.Players[i].IsConnected)
                {
                    ChangePersonType(Constants.Player, i.ToString(), null);
                }
            }

            StartGame();
        }
    }
}
