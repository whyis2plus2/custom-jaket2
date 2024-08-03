namespace Jaket.Net;

using Steamworks;
using Steamworks.Data;
using System;
using System.Linq;
using UnityEngine;

using Jaket.Assets;
using Jaket.IO;
using Jaket.UI.Dialogs;

/// <summary> Lobby controller with several useful methods and properties. </summary>
public class LobbyController
{
    static Chat chat => Chat.Instance;
    /// <summary> The current lobby the player is connected to. Null if the player is not connected to any lobby. </summary>
    public static Lobby? Lobby;
    public static bool Online => Lobby != null;
    public static bool Offline => Lobby == null;

    /// <summary> Id of the last lobby owner, needed to track the exit of the host and for other minor things. </summary>
    public static SteamId LastOwner;
    /// <summary> Whether the player owns the lobby. </summary>
    public static bool IsOwner;

    /// <summary> Whether a lobby is creating right now. </summary>
    public static bool CreatingLobby;
    /// <summary> Whether a list of public lobbies is being fetched right now. </summary>
    public static bool FetchingLobbies;

    /// <summary> Whether PvP is allowed in this lobby. </summary>
    public static bool PvPAllowed => Lobby?.GetData("pvp") == "True";
    /// <summary> Whether cheats are allowed in this lobby. </summary>
    // P-1 and P-2 boss doors like to get stuck after I die - so cheats are enabled there
    public static bool CheatsAllowed => 
        Lobby?.GetData("cheats") == "True" ||
        Tools.Scene == "Level P-1" ||
        Tools.Scene == "Level P-2";
    /// <summary> Whether mods are allowed in this lobby. </summary>
    public static bool ModsAllowed => true;
    /// <summary> Whether bosses must be healed after death in this lobby. </summary>
    public static bool HealBosses => Lobby?.GetData("heal-bosses") == "True";
    /// <summary> Number of percentages that will be added to the boss's health for each player. </summary>
    public static float PPP;

    /// <summary> Scales health to increase difficulty. </summary>
    public static void ScaleHealth(ref float health) => health *= 1f + Math.Min(Lobby?.MemberCount - 1 ?? 1, 1) * PPP;
    /// <summary> Whether the given lobby is created via Multikill. </summary>
    public static bool IsMultikillLobby(Lobby lobby) => lobby.Data.Any(pair => pair.Key == "mk_lobby");

    /// <summary> Creates the necessary listeners for proper work. </summary>
    public static void Load()
    {
        // get the owner id when entering the lobby
        SteamMatchmaking.OnLobbyEntered += lobby =>
        {
            if (lobby.Owner.Id != 0L) LastOwner = lobby.Owner.Id;

            if (IsMultikillLobby(lobby))
            {
                LeaveLobby();
                Bundle.Hud("lobby.mk");
            }
        };
        // and leave the lobby if the owner has left it
        SteamMatchmaking.OnLobbyMemberLeave += (lobby, member) =>
        {
            if (member.Id == LastOwner) LeaveLobby();
        };

        // put the level name in the lobby data so that it can be seen in the public lobbies list
        Events.OnLoaded += () => Lobby?.SetData("level", MapMap(Tools.Scene));
        // if the player exits to the main menu, then this is equivalent to leaving the lobby
        Events.OnMainMenuLoaded += () => LeaveLobby(false);
    }

    /// <summary> Is there a user with the given id among the members of the lobby. </summary>
    public static bool Contains(uint id) => Lobby?.Members.Any(member => member.Id.AccountId == id) ?? false;

    /// <summary> Returns the member at the given index or null. </summary>
    public static Friend? At(int index) => Lobby?.Members.ElementAt(Math.Min(Math.Max(index, 0), Lobby.Value.MemberCount));

    /// <summary> Returns the index of the local player in the lits of members. </summary>
    public static int IndexOfLocal() => Lobby?.Members.ToList().FindIndex(member => member.IsMe) ?? 0;

    #region control

    /// <summary> Asynchronously creates a new lobby with default settings and connects to it. </summary>
    public static void CreateLobby()
    {
        if (Lobby != null || CreatingLobby) return;
        Log.Debug("Creating a lobby...");

        CreatingLobby = true;
        SteamMatchmaking.CreateLobbyAsync(250).ContinueWith(task =>
        {
            CreatingLobby = false; IsOwner = true;
            Lobby = task.Result;

            Lobby?.SetJoinable(true);
            Lobby?.SetPrivate();
            Lobby?.SetData("jaket", "true");
            Lobby?.SetData("name", $"{SteamClient.Name}'s Lobby");
            Lobby?.SetData("level", MapMap(Tools.Scene));
            Lobby?.SetData("pvp", "True");
            Lobby?.SetData("cheats", "False");
            Lobby?.SetData("mods", "True");
            Lobby?.SetData("heal-bosses", "True");
        });
    }

    /// <summary> Leaves the lobby. If the player is the owner, then all other players will be thrown into the main menu. </summary>
    public static void LeaveLobby(bool loadMainMenu = true)
    {
        Log.Debug("Leaving the lobby...");

        if (Online) // free up resources allocated for packets that have not been sent
        {
            Networking.Server.Close();
            Networking.Client.Close();
            Pointers.Free();

            Lobby?.Leave();
            Lobby = null;
        }

        // load the main menu if the client has left the lobby
        if (!IsOwner && loadMainMenu) Tools.Load("Main Menu");

        Networking.Clear();
        Events.OnLobbyAction.Fire();
    }

    /// <summary> Opens Steam overlay with a selection of a friend to invite to the lobby. </summary>
    public static void InviteFriend() => SteamFriends.OpenGameInviteOverlay(Lobby.Value.Id);

    /// <summary> Asynchronously connects the player to the given lobby. </summary>
    public static void JoinLobby(Lobby lobby)
    {
        if (Lobby?.Id == lobby.Id) { Bundle.Hud("lobby.join-yourself"); return; }
        Log.Debug("Joining a lobby...");

        // leave the previous lobby before join the new, but don't load the main menu
        if (Online) LeaveLobby(false);

        lobby.Join().ContinueWith(task =>
        {
            if (task.Result == RoomEnter.Success)
            {
                IsOwner = false;
                Lobby = lobby;
            }
            else Log.Warning($"Couldn't join a lobby. Result is {task.Result}");
        });
    }

    #endregion
    #region codes

    /// <summary> Copies the lobby code to the clipboard. </summary>
    public static void CopyCode()
    {
        GUIUtility.systemCopyBuffer = Lobby?.Id.ToString();
        if (Online) Bundle.Hud("lobby.copied");
    }

    /// <summary> Joins by the lobby code from the clipboard. </summary>
    public static void JoinByCode()
    {
        if (ulong.TryParse(GUIUtility.systemCopyBuffer, out var code)) JoinLobby(new(code));
        else Bundle.Hud("lobby.failed");
    }

    #endregion
    #region browser

    /// <summary> Asynchronously fetches a list of public lobbies. </summary>
    public static void FetchLobbies(Action<Lobby[]> done)
    {
        FetchingLobbies = true;
        SteamMatchmaking.LobbyList.RequestAsync().ContinueWith(task =>
        {
            FetchingLobbies = false;
            done(task.Result.Where(l => l.Data.Any(p => p.Key == "jaket" || p.Key == "mk_lobby")).ToArray());
        });
    }

    /// <summary> Maps the map name so that it is more understandable to an average player. </summary>
    public static string MapMap(string map) => map switch
    {
        "Tutorial" => "Skill Issue",
        "uk_construct" => "box",
        "Endless" => "Goober Grind 2",
        "CreditsMuseum2" => "<b><i><size=35><color=#f6c>CREDIST</color></size></i></b>",

        // secret levels
        "Level 0-S"           => "Something <i><b><size=30>freaky</size></b></i>",
        "Level 1-S"           => "Puzzles!!!",
        "Level 2-S"           => "Mirage (NONONONONONONONONO)",
        "Level P-1" /* 3-S */ => "Pinos",
        "Level 4-S"           => "Cash Bazinga",
        "Level 5-S"           => "Pen Island",
        "Level P-2" /* 6-S */ => "Piss Man & Friends",
        "Level 7-S"           => "Maid Simulator 2024",

        // prelude
        "Level 0-1" => "PIPE CLIP LIVES",
        "Level 0-2" => "0-2",
        "Level 0-3" => "0-3",
        "Level 0-4" => "0-4",
        "Level 0-5" => "ultraballer",

        // limbo
        "Level 1-1" => "1-1",
        "Level 1-2" => "1-2",
        "Level 1-3" => "Based Level",
        "Level 1-4" => "Piss Baby",

        // lust
        "Level 2-1" => "2-1",
        "Level 2-2" => "Requiem Motif",
        "Level 2-3" => "2-3",
        "Level 2-4" => "Minos Corpse",

        // gluttony
        "Level 3-1" => "Minos Vore",
        "Level 3-2" => "<color=red>G</color><color=green>a</color><color=blue>y</color>briel",

        // greed
        "Level 4-1" => "4-1",
        "Level 4-2" => "4-2",
        "Level 4-3" => "Bad Level",
        "Level 4-4" => "Piss Baby Returns",

        // wrath
        "Level 5-1" => "Moist Cave",
        "Level 5-2" => "Jakito",
        "Level 5-3" => "Boat",
        "Level 5-4" => "Level 5-4",

        // heresy
        "Level 6-1" => "<color=#c00><b>racist</b></color> <color=red>G</color><color=green>a</color><color=blue>y</color>briel's foreplay",
        "Level 6-2" => "<color=#c00><b>racist</b></color> <color=red>G</color><color=green>a</color><color=blue>y</color>briel",

        // violence
        "Level 7-1" => "Garten of Midmid",
        "Level 7-2" => "War (Best Level)",
        "Level 7-3" => "LowTierGod Victims",
        "Level 7-4" => "Benjamin Gaming",


        // custom
        "UltrabusLmao" => "Ultrabus",

        _ => map.Substring("Level ".Length)
    };

    #endregion
}
