﻿module SrmApiModels
open System

type Profile = 
    { Id : Guid
      Forename : string
      Surname : string
      Rating : int
      ImageUrl : string
      Bio : string }

type SrmOutline = 
    { Profiles : Profile [] }