﻿using System;
using System.Collections.Generic;
using System.IO;
using NGeoHash;
using KML;

namespace geohash
{
    class Program
    {
        static double mLat = 41.87476071;
        static double mLon = -87.67198792;
        static int mLevel = 5;
        static double mRadius = 5000;
        static int mLimit = 200;
        public static DataBase m_dataBase;

        public static void Main(string[] args)
        {
            //CrimeDataTest();
            //BoundingBoxTest();
            //BoundingCircleTest();
            BoundingPolygonTest();
        }

        public static void BoundingBoxTest()
        {
            // Bounding box
            Coordinates coor1 = new Coordinates { Lat = 41.85776407, Lon = -87.73420671 };
            Coordinates coor2 = new Coordinates { Lat = 41.89993156, Lon = -87.60380377 };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string[] hashlist = GeoHash.Bboxes(coor1.Lat, coor1.Lon, coor2.Lat, coor2.Lon, mLevel);
            watch.Stop();
            var elapsedMs = watch.ElapsedMilliseconds;
            Console.WriteLine("Get bounding box, time elapsed: " + elapsedMs + " ms | " + hashlist.Length + " results get");
            KMLGenerator.GenerateKMLBoundingBoxes(hashlist, coor1, coor2, "bounding box");
        }

        public static void BoundingCircleTest()
        {
            Coordinates c = new Coordinates { Lat = mLat, Lon = mLon };

            Console.WriteLine("point latitude " + c.Lat + ", longitude " + c.Lon);
            var encoded = GeoHash.Encode(c.Lat, c.Lon, mLevel);
            Console.WriteLine("encoded = " + encoded);

            var decoded = GeoHash.Decode(encoded);
            var latitude = decoded.Coordinates.Lat;
            var longitude = decoded.Coordinates.Lon;
            Console.WriteLine("decoded box latitude " + latitude + ", longitude " + longitude);

            BoundingBox box = GeoHash.DecodeBbox(encoded);
            var maxLat = box.Maximum.Lat;
            var minLat = box.Minimum.Lat;
            var maxLon = box.Maximum.Lon;
            var minLon = box.Minimum.Lon;

            // Measure the box size in meters
            var oneSide = DataBase.Measure(maxLat, minLon, minLat, minLon);
            var anotherSide = DataBase.Measure(maxLat, maxLon, maxLat, minLon);

            // Bounding circle
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string[] hashList = DataBase.Bcircle(c.Lat, c.Lon, mRadius, mLevel);
            watch.Stop();
            var elapsedMs = watch.ElapsedMilliseconds;

            //foreach (var h in hashList)
            //{
            //    Console.WriteLine(h);
            //}

            Console.WriteLine("box size: " + oneSide + " meters * " + anotherSide + " meters");
            Console.WriteLine("bounding circle radius " + mRadius + " meters, level " + mLevel);
            Console.WriteLine("Get bounding circle, time elapsed: " + elapsedMs + " ms | " + hashList.Length + " results get");
            string filename = "bounding circle" + c.Lat.ToString() + "-" + c.Lon.ToString() + "-" + mRadius.ToString() + "-" + mLevel.ToString();
            KMLGenerator.GenerateKMLBoundingCircle(hashList, c.Lat, c.Lon, mRadius, filename);
            Console.WriteLine("save as file name " + filename);
        }

        public static void CrimeDataTest()
        {
            DataBase database = new DataBase();
            m_dataBase = database;
            var watch0 = System.Diagnostics.Stopwatch.StartNew();
            // TODO: takes 8895 ms for database small
            //AddDatasetSmall(database);
            // TODO: takes 34 seconds for load dataset1
            AddDataset1(database);
            //AddDataset2(database);
            watch0.Stop();
            Console.WriteLine("Process data takes " + watch0.ElapsedMilliseconds + " ms");
            database.Display();

            ///
            /// Test get coordinates in bounding box
            /// TODO: search coordinates in bbox take much more time than searching in bcircle
            var watch2 = System.Diagnostics.Stopwatch.StartNew();
            var c2 = database.BboxCoordinates(41.85776407, -87.73420671, 41.89993156, -87.60380377, mLevel);
            watch2.Stop();
            var elapsedMs2 = watch2.ElapsedMilliseconds;
            Console.WriteLine("BboxCoordinates, Time elapsed: " + elapsedMs2 + " ms | " + c2.Length + " results get");
            KMLGenerator.GenerateKMLcoordinates(c2, "box coordinates");

            ///
            /// Display search process
            ///


            ///
            /// Test get coordinates in bounding circle
            /// 
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Coordinates[] c = database.BcircleCoordinates(mLat, mLon, mRadius, mLevel, mLimit);
            watch.Stop();
            var elapsedMs = watch.ElapsedMilliseconds;
            Console.WriteLine("BcircleCoordinates, Time elapsed: " + elapsedMs + " ms | " + c.Length + " results get");
            KMLGenerator.GenerateKMLcoordinates(c, "circle coordinates");

            ///
            /// Display search process
            ///
        }

        public static void BoundingPolygonTest()
        {
            Coordinates[] polygon1 = { 
                new Coordinates { Lat = 0, Lon = 0 },
                new Coordinates { Lat = 10, Lon = 0 },
                new Coordinates { Lat = 10, Lon = 10 },
                new Coordinates { Lat = 0, Lon = 10 }
            };
            Coordinates p = new Coordinates { Lat = 20, Lon = 20 };
            Console.WriteLine(DataBase.PointInPolygon(p, polygon1));

            p = new Coordinates { Lat = 5, Lon = 5 };
            Console.WriteLine(DataBase.PointInPolygon(p, polygon1));

            Coordinates[] polygon2 = { 
                new Coordinates { Lon = 0, Lat = 0 },
                new Coordinates { Lon = 5, Lat = 5 },
                new Coordinates { Lon = 5, Lat = 0 }
            };
            p = new Coordinates { Lon = 3, Lat = 3 };
            Console.WriteLine(DataBase.PointInPolygon(p, polygon2));

            p = new Coordinates { Lon = 5, Lat = 1 };
            Console.WriteLine(DataBase.PointInPolygon(p, polygon2));

            p = new Coordinates { Lon = 8, Lat = 1};
            Console.WriteLine(DataBase.PointInPolygon(p, polygon2));

            Coordinates[] polygon3 = {
                new Coordinates { Lon = 0, Lat = 0 },
                new Coordinates { Lon = 10, Lat = 0 },
                new Coordinates { Lon = 10, Lat = 10 },
                new Coordinates { Lon = 0, Lat = 10 }
            };
            p = new Coordinates { Lon = -1, Lat = 10 };
            Console.WriteLine(DataBase.PointInPolygon(p, polygon3));
        }

        static void AddDatasetSmall(DataBase database)
        {
            string path = "../../../Resources/Crimes_-_2019.csv";
            string[] lines = System.IO.File.ReadAllLines(path);
            //Console.WriteLine(lines[1]);
            int count = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                string[] columns = line.Split(',');
                string LatStr = columns[19];
                string LonStr = columns[20];
                string LocationDescription = columns[11];
                string id = columns[0];
                if (LatStr != "" && LonStr != "")
                {
                    double lat;
                    double lon;
                    //Console.WriteLine(LatStr + LonStr);
                    // Filter out errors
                    try
                    {
                        lat = Convert.ToDouble(LatStr);
                        lon = Convert.ToDouble(LonStr);
                    }
                    catch
                    {
                        continue;
                    }
                    Coordinates c = new Coordinates {
                        Lat = lat,
                        Lon = lon,
                        Id = id,
                        LocationDescription = LocationDescription
                    };
                    database.Add(c);
                    count++;
                }

                if(count > 5000)
                {
                    return;
                }
            }
            
        }

        static void AddDataset1(DataBase database)
        {
            string path = "../../../Resources/Crimes_-_2019.csv";
            string[] lines = System.IO.File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                string[] columns = line.Split(',');
                string LatStr = columns[19];
                string LonStr = columns[20];
                string LocationDescription = columns[7];
                string id = columns[0];
                if (LatStr != "" && LonStr != "")
                {
                    double lat;
                    double lon;
                    Console.WriteLine(LatStr + LonStr+','+id +','+ LocationDescription);
                    // Filter out errors
                    try
                    {
                        lat = Convert.ToDouble(LatStr);
                        lon = Convert.ToDouble(LonStr);
                    }
                    catch
                    {
                        continue;
                    }
                    Coordinates c = new Coordinates {
                        Lat = lat,
                        Lon = lon,
                        Id = id,
                        LocationDescription = LocationDescription };
                    database.Add(c);
                }
            }
        }

        static void AddDataset2(DataBase database)
        {
            string path = "../../../Resources/crime.csv";
            string[] lines2 = System.IO.File.ReadAllLines(path);
            //Console.WriteLine(lines2[1]);

            for (int i = 1; i < lines2.Length; i++)
            {
                string line = lines2[i];
                string[] columns = line.Split(',');
                string LatStr = columns[14];
                string LonStr = columns[15];
                if (LatStr != "" && LonStr != "")
                {
                    double lat;
                    double lon;
                    //Console.WriteLine(LatStr + LonStr);
                    // Filter out errors
                    try
                    {
                        lat = Convert.ToDouble(LatStr);
                        lon = Convert.ToDouble(LonStr);
                    }
                    catch
                    {
                        continue;
                    }
                    Coordinates c = new Coordinates
                    {
                        Lat = lat,
                        Lon = lon,
                        Id = "",
                        LocationDescription = ""
                    };
                    database.Add(c);
                }
            }
        }
    }
}
