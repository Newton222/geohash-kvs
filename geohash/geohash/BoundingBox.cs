﻿namespace NGeoHash
{
    public class BoundingBox
    {
        public Coordinates Minimum { get; set; }
        public Coordinates Maximum { get; set; }
        public string hash { get; set; }
    }
}