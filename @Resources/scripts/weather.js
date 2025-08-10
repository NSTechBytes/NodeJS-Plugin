// weather.js
const https = require('https');

let temperature = 0; // default until fetched

function initialize() {
  const lat = RM.GetVariable("Latitude"); // Lahore latitude
  const lon = RM.GetVariable("Longitude"); // Lahore longitude
  const url = `https://api.open-meteo.com/v1/forecast?latitude=${lat}&longitude=${lon}&current_weather=true`;

  https.get(url, (res) => {
    let data = '';

    res.on('data', chunk => {
      data += chunk;
    });

    res.on('end', () => {
      try {
        const parsed = JSON.parse(data);
        const cw = parsed.current_weather;
        temperature = cw.temperature; // store for update()
      } catch (err) {
        RM.LogError('Error parsing weather data: ' + err.message);
      }
    });

  }).on('error', (err) => {
    RM.LogError('Error fetching weather: ' + err.message);
  });
}

function update() {
  return temperature; // return the last fetched temperature
}

module.exports = { initialize, update };
