﻿using NGeoHash;
using System;
namespace server.Models
{
    public class PolygonSearchRequestModelClass
    {
        public Coordinates[] Vertices { get; set; }
        public int Level { get; set; }
    }
}