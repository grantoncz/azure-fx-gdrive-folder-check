# Use an official Node.js runtime as a parent image
FROM node:14

# Install Azure Functions Core Tools
RUN npm install -g azure-functions-core-tools@4 --unsafe-perm true

# Set the working directory
WORKDIR /usr/src/app

# Copy the current directory contents into the container at /usr/src/app
COPY . .

# Install any needed packages specified in package.json
RUN npm install

# Make port 7071 available to the world outside this container
EXPOSE 7071

# Run the Azure Functions Core Tools
CMD ["func", "start"]