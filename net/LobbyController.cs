namespace Jaket.Net;

using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

using Jaket.UI;

/// <summary> Lobby controller with several useful methods. </summary>
public class LobbyController
{
    /// <summary> The current lobby the player is connected to. Null if the player is not connected to a lobby. </summary>
    public static Lobby? Lobby;
    /// <summary> Lobby owner or the player's SteamID if the lobby is null. </summary>
    public static SteamId Owner => Lobby == null ? SteamClient.SteamId : Lobby.Value.Owner.Id;
    /// <summary> Id of the last lobby owner, needed to track the exit of the host. </summary>
    public static SteamId LastOwner;

    /// <summary> Whether a lobby is creating right now. </summary>
    public static bool CreatingLobby;
    /// <summary> Whether a list of public lobbies is being fetched right now. </summary>
    public static bool FetchingLobbies;
    /// <summary> Whether the player owns the lobby. </summary>
    public static bool IsOwner;

    /// <summary> Creates the necessary listeners for proper work with a lobby. </summary>
    public static void Load()
    {
        // get the owner id when entering the lobby
        SteamMatchmaking.OnLobbyEntered += lobby =>
        {
            if (lobby.Owner.Id != 0L) LastOwner = lobby.Owner.Id;
        };

        // and leave the lobby if the owner has left it
        SteamMatchmaking.OnLobbyMemberLeave += (lobby, member) =>
        {
            if (member.Id == LastOwner)
            {
                LeaveLobby(); // leave the lobby to avoid bugs and load into the main menu
                SceneHelper.LoadScene("Main Menu");
            }
        };

        // put the level name in the lobby data so that it can be seen in the public lobbies list
        SceneManager.sceneLoaded += (scene, mode) => Lobby?.SetData("level", MapMap(SceneHelper.CurrentScene));
    }

    #region control

    /// <summary> Asynchronously creates a new lobby and connects to it. </summary>
    public static void CreateLobby(Action done)
    {
        if (Lobby != null || CreatingLobby) return;

        var task = SteamMatchmaking.CreateLobbyAsync(8);
        CreatingLobby = true;

        task.GetAwaiter().OnCompleted(() =>
        {
            Lobby = task.Result.Value;
            IsOwner = true;

            Lobby?.SetJoinable(true);
            Lobby?.SetPrivate();
            Lobby?.SetData("level", MapMap(SceneHelper.CurrentScene));

            CreatingLobby = false;
            done();

            // update the discord activity so everyone can know I've been working hard
            DiscordController.Instance.FetchSceneActivity(SceneHelper.CurrentScene);

            // update the color of the hand
            Networking.LocalPlayer.UpdateWeapon();

            // run the game in the background to stop freezing the server
            Application.runInBackground = true;
        });
    }

    /// <summary> Leaves the lobby, if the player is the owner, then all other players will be thrown into the main menu. </summary>
    public static void LeaveLobby()
    {
        Lobby?.Leave();
        Lobby = null;

        // if the client has left the lobby, then load the main menu
        if (!IsOwner && SceneHelper.CurrentScene != "Main Menu") SceneHelper.LoadScene("Main Menu");

        // destroy all network objects
        Networking.Clear();

        // remove mini-ads if the player is playing alone
        DiscordController.Instance.FetchSceneActivity(SceneHelper.CurrentScene);

        // return the color of the hands
        Networking.LocalPlayer.UpdateWeapon();

        // return as it was, don't run the game in the background
        Application.runInBackground = false;
    }

    /// <summary> Opens a steam overlay with a selection of a friend to invite to the lobby. </summary>
    public static void InviteFriend() => SteamFriends.OpenGameInviteOverlay(Lobby.Value.Id);

    /// <summary> Asynchronously connects the player to the given lobby. </summary>
    public static async void JoinLobby(Lobby lobby)
    {
        if (Lobby?.Id == lobby.Id)
        {
            UI.SendMsg(
@"""Why would you want to join yourself?!""
<size=20><color=grey>(c) xzxADIxzx</color></size>");
            return;
        }

        if (Lobby != null) LeaveLobby();
        Debug.Log("Joining to the lobby...");

        var enter = await lobby.Join();
        if (enter == RoomEnter.Success)
        {
            Lobby = lobby;
            IsOwner = false;

            // run the game in the background so that the client does not have lags upon returning from AFK
            Application.runInBackground = true;
        }
        else UI.SendMsg(
@"<size=20><color=red>Couldn't connect to the lobby, it's a shame.</color></size>
Maybe it was closed or you were blocked ,_,");

        // update the interface to match the new state
        LobbyTab.Instance.Rebuild();

        // update the discord activity so everyone can know I've been working hard
        DiscordController.Instance.FetchSceneActivity(SceneHelper.CurrentScene);
    }

    #endregion
    #region members

    /// <summary> Returns a list of nicknames of players currently typing. </summary>
    public static List<string> TypingPlayers()
    {
        List<string> list = new();

        if (Chat.Instance.Shown) list.Add("You");
        Networking.EachPlayer(player =>
        {
            if (player.typing) list.Add(player.nickname);
        });

        return list;
    }

    /// <summary> Iterates each lobby member. </summary>
    public static void EachMember(Action<Friend> cons)
    {
        foreach (var member in Lobby.Value.Members) cons(member);
    }

    /// <summary> Iterates each lobby member except its owner. </summary>
    public static void EachMemberExceptOwner(Action<Friend> cons)
    {
        foreach (var member in Lobby.Value.Members)
        {
            // usually this method is used by the server to send packets, because it doesn't make sense to send packets to itself
            if (member.Id != Lobby.Value.Owner.Id) cons(member);
        }
    }

    /// <summary> Iterates each lobby member, except for its owner and one more SteamID. </summary>
    public static void EachMemberExceptOwnerAnd(SteamId id, Action<Friend> cons)
    {
        foreach (var member in Lobby.Value.Members)
        {
            // usually this method is used by the server to forward packets from one of the clients
            if (member.Id != Lobby.Value.Owner.Id && member.Id != id) cons(member);
        }
    }

    #endregion
    #region codes

    /// <summary> Copies the lobby code to the clipboard. </summary>
    public static void CopyCode()
    {
        GUIUtility.systemCopyBuffer = Lobby?.Id.ToString();
        if (Lobby != null) UI.SendMsg(
@"<size=20><color=#00FF00>The lobby code has been successfully copied to the clipboard!</color></size>
Send it to your friends so they can join you :D");
    }

    /// <summary> Joins by the lobby code from the clipboard. </summary>
    public static void JoinByCode()
    {
        if (ulong.TryParse(GUIUtility.systemCopyBuffer, out var code)) JoinLobby(new(code));
        else UI.SendMsg(
@"<size=20><color=red>Could not find the lobby code on your clipboard!</color></size>
Make sure it is copied without spaces :(");
    }

    #endregion
    #region browser

    /// <summary> Asynchronously fetches a list of public lobbies. </summary>
    public static void FetchLobbies(Action<Lobby[]> done)
    {
        var task = SteamMatchmaking.LobbyList.RequestAsync();
        FetchingLobbies = true;

        task.GetAwaiter().OnCompleted(() =>
        {
            FetchingLobbies = false;
            done(task.Result);
        });
    }

    /// <summary> Maps the maps names so that they are more understandable to the average player. </summary>
    public static string MapMap(string map) => map switch
    {
        "uk_construct" => "Sandbox",
        "Endless" => "Myth",
        "CreditsMuseum2" => "Museum",
        _ => map.Substring("Level ".Length)
    };

    #endregion
}