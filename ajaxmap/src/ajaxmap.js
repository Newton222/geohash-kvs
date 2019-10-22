'use strict';
function AjaxMap(mapid) 
{
    console.log('map started');

    this.lat = 41.87476071;
    this.lon = -87.67198792;
    this.range = 800;
    this.level = 6;
    this.limit = 0;

    // setup basic map
    this.map = L.map(mapid).setView([this.lat, this.lon], 15);
    this.layerGroup = L.layerGroup().addTo(this.map);
    this.tempLayerGroup = L.layerGroup().addTo(this.map);
    L.tileLayer('https://api.tiles.mapbox.com/v4/{id}/{z}/{x}/{y}.png?access_token=pk.eyJ1IjoibWFwYm94IiwiYSI6ImNpejY4NXVycTA2emYycXBndHRqcmZ3N3gifQ.rJcFIG214AriISLbB6B5aw', {
        maxZoom: 18,
        attribution: 'Map data &copy; <a href="https://www.openstreetmap.org/">OpenStreetMap</a> contributors, ' +
            '<a href="https://creativecommons.org/licenses/by-sa/2.0/">CC-BY-SA</a>, ' +
            'Imagery © <a href="https://www.mapbox.com/">Mapbox</a>',
        id: 'mapbox.streets'
    }).addTo(this.map);

    this.configMap();
}

AjaxMap.prototype.configMap = function()
{
    let that = this;

    // popup
    let popup = L.popup();
    function onMapClick(e) {
        popup
            .setLatLng(e.latlng)
            .setContent("You clicked the map at " + e.latlng.toString() + "<input type=\"button\" id=\"popup-submit\" value=\"center here\">")
            .openOn(that.map);

        document.getElementById("popup-submit").onclick = function (event) {
            that.lat = e.latlng.lat;
            that.lon = e.latlng.lng;
            that.updateMap();
        }
    }
    this.map.on('click', onMapClick);

    // get input
    let lat = document.getElementById("input_lat");
    let lon = document.getElementById("input_lon");
    let range = document.getElementById("input_range");
    let limit = document.getElementById("input_limit");
    let message = document.getElementById("message");

    lat.oninput = function(event) {
        if (lat.value === "" || isNaN(Number(lat.value)))
        {
            console.log("error lat");
            message.innerHTML = "error lat";
            return;
        }
        that.lat = Number(lat.value);
    };

    lon.oninput = function(event) {
        if (lon.value === "" || isNaN(Number(lon.value)))
        {
            console.log("error lon");
            message.innerHTML = "error lon";
            return;
        }
        that.lon = Number(lon.value);
    };

    range.oninput = function(event) {
        if (range.value === "" || isNaN(Number(range.value)))
        {
            console.log("error range");
            message.innerHTML = "error range";
            return;
        }
        that.range = Number(range.value);
    };

    limit.oninput = function(event) {
        if (limit.value === "" || !Number.isInteger(Number(limit.value)))
        {
            console.log("error limit");
            message.innerHTML = "error limit";
            return;
        }
        that.limit = Number(limit.value);
    };

    // update the value on the level input slider
    let slider = document.getElementById("input_level");
    let output = document.getElementById("input_level_value");
    output.innerHTML = slider.value;
    slider.oninput = function() {
        output.innerHTML = this.value;
        that.level = Number(this.value);
    };

    document.getElementById("map1_search").onclick = function(event) {
        console.log(event);
        that.updateMap();
    }
}

AjaxMap.prototype.updateMap = function()
{
    //console.log('update with value: lat '+ this.lat + ' lon ' + this.lon + ' range ' + this.range + ' search level ' + this.level + ' limit ' + this.limit);
    document.getElementById("message").innerHTML = "";
    // clear map elements
    this.layerGroup.clearLayers();
    this.tempLayerGroup.clearLayers();

    // update input area
    document.getElementById("input_range").value = this.range;
    document.getElementById("input_limit").value = this.limit;
    document.getElementById("input_lat").value = this.lat;
    document.getElementById("input_lon").value = this.lon;
    document.getElementById("input_level").value = this.level;
    document.getElementById("input_level_value").innerHTML = this.level;
    
    // draw new elements
    // draw center
    let marker = L.marker([this.lat, this.lon]).addTo(this.layerGroup)
        .bindTooltip("<b>Center</b>", {direction:"top"}).openTooltip();
    // draw range
    let circle = L.circle([this.lat, this.lon], this.range, {
        color: 'red',
        fillColor: '#f03',
        fillOpacity: 0.1
    }).addTo(this.layerGroup);

    // draw coordinates
    let that = this;
    let request = new Object();
    request.Lat = this.lat;
    request.Lon = this.lon;
    request.Range = this.range;
    request.Level = this.level;
    request.Limit = this.limit;
    console.log('getCoordinates request server: '+ JSON.stringify(request));
    $.ajax({
        url: "https://localhost:5001/CircleSearchCoordinates",
        method: "POST",
        contentType: "application/json",
        data: JSON.stringify(request),
        success: function(result) {
            console.log(result.length + " coordinates returned");
            document.getElementById("map1_coordinates_count").innerHTML = result.length+ " coordinates get";
            that.drawCoordinates(result);
        },
        error: function(jqxhr, status, exception) {
            console.log("error get "+ jqxhr + status + exception);
        }
    });
    // draw bounding boxes
    console.log('getBoundingBoxes request server: '+ JSON.stringify(request));
    $.ajax({
        url: "https://localhost:5001/CircleSearchBboxes",
        method: "POST",
        contentType: "application/json",
        data: JSON.stringify(request),
        success: function(result) {
            console.log(result.length + " boxes returned");
            that.drawBoundingBoxes(result);
        },
        error: function(jqxhr, status, exception) {
            console.log("error get "+ jqxhr + status + exception);
        }
    });
}

AjaxMap.prototype.onClickMarker = function(c)
{
    this.tempLayerGroup.clearLayers();
    var that = this;
    //console.log(c);
    let request = new Object();
    request.SearchLat = this.lat;
    request.SearchLon = this.lon;
    request.SelectLat = c.lat;
    request.SelectLon = c.lon;
    request.Range = this.range;
    request.Level = this.level;

    var coordinatesInRange;
    var coordinatesOutOfRange;
    var hash;
    $.ajax({
        url: "https://localhost:5001/DisplayBoundingCircleSearchProcess",
        method: "POST",
        contentType: "application/json",
        data: JSON.stringify(request),
        success: function(data) {
            let result = JSON.parse(data);
            console.log(result.CoordinatesInRange.length + " in range");
            coordinatesInRange = result.CoordinatesInRange;
            console.log(result.CoordinatesOutOfRange.length + " out of range");
            coordinatesOutOfRange = result.CoordinatesOutOfRange;
            console.log("select hash " + result.Boxhash);
            hash = result.Boxhash;
            coordinatesOutOfRange.forEach(element => {
                L.marker([element.Lat, element.Lon], {opacity:0.5}).addTo(that.tempLayerGroup);
            });
        },
        error: function(jqxhr, status, exception) {
            console.log("error get "+ jqxhr + status + exception);
        }
    });

    let html = `
<p>
selected marker ${c.lat}, ${c.lon}, ${c.id}, ${c.locationDescription}, ${c.description}
</p>
`;
    
    document.getElementById("map1_info").innerHTML = html;
}



// takes in an array of coordinates json
AjaxMap.prototype.drawCoordinates = function(coordinates)
{
    let that = this;
    for (let i=0; i<coordinates.length; i++)
    {
        let c = coordinates[i];
        L.marker([c.lat, c.lon]).addTo(that.layerGroup)
        .bindPopup(c.id+','+c.locationDescription+','+c.description)
        .on("click", function(ev) {
            that.onClickMarker(c);
        });
    }
}

// takes in an array of bounding box json
AjaxMap.prototype.drawBoundingBoxes = function(boxes)
{
    for (let i=0; i<boxes.length; i++)
    {
        let box = boxes[i];
        let max = box.maximum;
        let min = box.minimum;
        let bounds = [[min.lat, min.lon],[max.lat, max.lon]];
        L.rectangle(bounds, {color: "#ff8e2c", weight: 2})
        .bindTooltip(box.hash,{permanent:true, direction:"center", opacity:0.6})
        .openTooltip().addTo(this.layerGroup);
    }
}