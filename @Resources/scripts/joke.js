const axios = require('axios');

async function update() {
  try {
    const response = await axios.get('https://v2.jokeapi.dev/joke/Any?type=single');
    if (response.data && !response.data.error) {
      return response.data.joke;
    }
    return 'Could not fetch joke.';
  } catch (error) {
    console.error('Joke API Error:', error.message);
    return 'API request failed.';
  }
}

module.exports = {
  update
};