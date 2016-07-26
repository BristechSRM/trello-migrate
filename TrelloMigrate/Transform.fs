﻿module Transform

open System.Text.RegularExpressions
open TrelloModels
open SrmApiModels
open TransformationModels
open System

module private Handle = 
    let createEmailHandle (email : string) = 
        { ProfileId = Guid.NewGuid(); Type = "email"; Identifier = email.ToLowerInvariant() }

module private Profile = 

    //Note : Currently failing if any members (Admin) have a missing name
    //Take first string of split as first name, take the rest as last name
    let private parseFullName (fullName : string) = 
        let split = fullName.Split()
        match split.Length with
        | 1 -> { Forename = split.[0]; Surname = "" }
        | x when x > 1 -> { Forename = split.[0]; Surname = split |> Array.skip 1 |> String.concat " " }
        | _ -> 
            let message = "Full name of is missing? All Admins must have a full name, and all speaker cards must have the speaker name filled in. Input value was: " + fullName
            failwith message   

    let private defaultImageUrl = "https://placebear.com/50/50"

    let private getImageUrl avatarHash =   
        if String.IsNullOrWhiteSpace avatarHash then
            defaultImageUrl
        else 
            sprintf "https://trello-avatars.s3.amazonaws.com/%s/50.png" avatarHash

    let private create imageUrl (names : Names) : Profile = 
        { Id = Guid.Empty 
          Forename = names.Forename
          Surname = names.Surname
          Rating = 1 
          ImageUrl = imageUrl
          Bio = String.Empty }

    let fromMember (basicMember : BasicMember) = 
        parseFullName basicMember.FullName
        |> create (getImageUrl basicMember.AvatarHash)

    let fromNameString (fullName : string) = 
        parseFullName fullName
        |> create defaultImageUrl

module private Member = 
    let private nameToScottLogicEmail (forename : string) (surname : string) = 
        if String.IsNullOrWhiteSpace surname then
            forename + "@scottlogic.co.uk"
        else 
            let firstNameFirstLetter = forename.Chars(0)
            let lastName = surname.Split() |> Array.last
            sprintf "%c%s@scottlogic.co.uk" firstNameFirstLetter lastName

    let createProfileWithReferenceId (basicMember : BasicMember) : ProfileWithReferenceId = 
        let profile = Profile.fromMember basicMember
        let handle = nameToScottLogicEmail profile.Forename profile.Surname |> Handle.createEmailHandle
        { ReferenceId = basicMember.Id
          ProfileWithHandles = { Profile = profile; Handles = [| handle |] }}

module private ParsedCard = 
    let createSession (parsedCard : ParsedCard) = 
        { Id = Guid.Empty 
          Title = parsedCard.TalkData 
          Description = String.Empty
          Status = String.Empty
          Date = None
          SpeakerId = Guid.Empty 
          AdminId = None
          DateAdded = None }

    let createSpeakerProfileWithHandles (parsedCard : ParsedCard) = 
        let speakerProfile = Profile.fromNameString parsedCard.SpeakerName
        let handles = 
            match parsedCard.SpeakerEmail with
            | Some email -> [| Handle.createEmailHandle email |]
            | None -> [||]

        { Profile = speakerProfile; Handles = handles }

module private Card = 

    let private tryParseCardName (cardName : string) = 
        let tryGetValue (group : Group) = 
            if group.Success && not <| String.IsNullOrWhiteSpace group.Value then Some <| group.Value.Trim()
            else None
        
        let m = Regex.Match(cardName, "(?<name>[^\[\]]* *)\[(?<email>.*)\] *\((?<talk>.*)\)(?<extra>.*)?$", RegexOptions.ExplicitCapture)
        if m.Success && m.Groups.["name"].Success && not <| String.IsNullOrWhiteSpace m.Groups.["name"].Value then 
            { SpeakerName = m.Groups.["name"].Value.Trim() 
              SpeakerEmail = tryGetValue m.Groups.["email"] 
              TalkData = m.Groups.["talk"].Value.Trim() }
            |> Some
        else None

    let private tryPickAdminId (admins : ProfileWithReferenceId []) (card : BasicCard) = 
        match card.IdMembers with
        | [||]  -> None
        | [| memberId |] -> 
            //If the member isn't in the admin list, it will be ignored. 
            admins |> Array.tryPick(fun a -> if a.ReferenceId = memberId then Some memberId else None)
        | _ -> 
            failwith <| sprintf "Card: %A had multiple members attached. Please remove additional members so that there is one admin per card" card

    let tryParseToSessionAndSpeaker (admins : ProfileWithReferenceId []) (card : BasicCard) = 
        match tryParseCardName card.Name with
        | Some parsedCard -> 
            Some { Session = ParsedCard.createSession parsedCard
                   Speaker = ParsedCard.createSpeakerProfileWithHandles parsedCard 
                   CardTrelloId = card.Id
                   AdminTrelloId = tryPickAdminId admins card }
        | None -> 
            printfn "Card with title:\n'%s' \nwas ingored because it did not match the accepted format of \n'speaker name[speakeremail](Talk title, brief or possible topic)" card.Name
            None        

let private splitProfilesFromSessions (admins : ProfileWithReferenceId []) (sessionsAndSpeakers : SessionSpeakerAndTrelloIds []) =
    //TODO perform a merge of speaker and admin information to make sure no information is lost. Currently extra handles would be dropped. 
    let sessions, nonAdminSpeakerOptions = 
        sessionsAndSpeakers
        |> Array.map (fun ss -> 
            let foundSpeakerAsAdmin = 
                admins |> Array.tryFind(fun a -> a.ProfileWithHandles.Profile.Forename = ss.Speaker.Profile.Forename && a.ProfileWithHandles.Profile.Surname = ss.Speaker.Profile.Surname)
            match foundSpeakerAsAdmin with 
            | Some adminWithRef -> 
                {Session = ss.Session; SpeakerTrelloId = adminWithRef.ReferenceId; AdminTrelloId = ss.AdminTrelloId}, None
            | None -> 
                {Session = ss.Session; SpeakerTrelloId = ss.CardTrelloId; AdminTrelloId = ss.AdminTrelloId}, Some {ReferenceId = ss.CardTrelloId; ProfileWithHandles = ss.Speaker})
        |> Array.unzip
    let nonAdminSpeakers = nonAdminSpeakerOptions |> Array.choose id
    let profiles = admins |> Array.append nonAdminSpeakers |> Array.map(fun pri -> pri.ReferenceId, pri.ProfileWithHandles) |> Map.ofArray
    profiles, sessions

let toSrmModels (board : BoardSummary) = 
    let adminProfiles = board.Members |> Array.map Member.createProfileWithReferenceId
    let sessionsAndSpeakers = board.BasicCards |> Array.choose (Card.tryParseToSessionAndSpeaker adminProfiles)
    let profiles, sessions = splitProfilesFromSessions adminProfiles sessionsAndSpeakers
    { Profiles = profiles
      Sessions = sessions }   