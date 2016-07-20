﻿module TrelloModels
open System

type BasicCard = 
    { Id : string
      Name : string
      Due : DateTime option
      IdMembers : string [] }

type BasicMember = 
    { Id : string
      Username : string
      FullName : string
      AvatarHash : string }

type GroupedMembers = 
    { Members : BasicMember []
      IgnoredMembers : BasicMember [] }

type BoardSummary = 
    { BasicCards : BasicCard [] 
      GroupedMembers : GroupedMembers }
