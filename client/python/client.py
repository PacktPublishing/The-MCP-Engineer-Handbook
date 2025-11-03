#!/usr/bin/env python3
"""
MCP Client with Ollama Integration - HTTP Transport

This is a simple MCP client that connects to an MCP server and integrates with
Ollama to provide AI-powered chat with tool calling capabilities.
"""

import asyncio
from mcp.client.session import ClientSession
from mcp.client.streamable_http import streamablehttp_client
import ollama


async def main():
    """Main entry point for the client."""
    # Set up MCP client with HTTP transport
    async with streamablehttp_client(url="http://localhost:5000") as (read_stream, write_stream, _):
        async with ClientSession(
            read_stream=read_stream,
            write_stream=write_stream
        ) as session:
            # Initialize the session
            await session.initialize()
            
            # List available tools from the MCP server
            tools_result = await session.list_tools()
            tools = tools_result.tools
            
            print("Connected to MCP server with tools:")
            for tool in tools:
                print(f"  - {tool.name}: {tool.description}")
            print()
            
            # Convert MCP tools to Ollama tool format
            ollama_tools = []
            for tool in tools:
                ollama_tool = {
                    "type": "function",
                    "function": {
                        "name": tool.name,
                        "description": tool.description or "",
                        "parameters": tool.inputSchema
                    }
                }
                ollama_tools.append(ollama_tool)
            
            # Set up the Ollama client
            model = "qwen2.5:1.5b"
            client = ollama.AsyncClient(host="http://127.0.0.1:11434")
            
            # Start the conversation
            messages = []
            
            while True:
                # Get user prompt
                print("Your prompt:")
                user_prompt = input()
                
                if not user_prompt.strip():
                    continue
                
                # Add user message to history
                messages.append({
                    "role": "user",
                    "content": user_prompt
                })
                
                # Stream the AI response
                print("AI Response:")
                full_response = ""
                
                # Call Ollama with tools
                response = await client.chat(
                    model=model,
                    messages=messages,
                    tools=ollama_tools,
                    stream=True
                )
                
                # Process streaming response
                tool_calls_list = None
                async for chunk in response:
                    if chunk.message.content:
                        print(chunk.message.content, end="", flush=True)
                        full_response += chunk.message.content
                    
                    # Capture tool calls (usually in the last chunk)
                    if chunk.message.tool_calls:
                        tool_calls_list = chunk.message.tool_calls
                
                print()  # New line after response
                
                # Handle tool calls after streaming is complete
                if tool_calls_list:
                    # Add assistant message with tool calls
                    messages.append({
                        "role": "assistant",
                        "content": full_response,
                        "tool_calls": tool_calls_list
                    })
                    
                    for tool_call in tool_calls_list:
                        tool_name = tool_call.function.name
                        tool_args = tool_call.function.arguments
                        
                        print(f"[Calling tool: {tool_name}]")
                        
                        # Call the tool through MCP
                        try:
                            result = await session.call_tool(
                                name=tool_name,
                                arguments=tool_args
                            )
                            
                            # Extract text content from result
                            tool_result = ""
                            for content in result.content:
                                if hasattr(content, 'text'):
                                    tool_result += content.text
                            
                            print(f"[Tool result: {tool_result}]")
                            
                            # Add tool result to messages
                            messages.append({
                                "role": "tool",
                                "content": tool_result
                            })
                            
                        except Exception as e:
                            print(f"[Error calling tool: {e}]")
                    
                    # Get final response from Ollama after tool calls
                    print("AI Response:")
                    final_response = await client.chat(
                        model=model,
                        messages=messages,
                        tools=ollama_tools,
                        stream=True
                    )
                    
                    full_final = ""
                    async for final_chunk in final_response:
                        if final_chunk.message.content:
                            print(final_chunk.message.content, end="", flush=True)
                            full_final += final_chunk.message.content
                    
                    # Add final assistant message
                    messages.append({
                        "role": "assistant",
                        "content": full_final
                    })
                    print()
                else:
                    # No tool calls, just add the assistant response
                    messages.append({
                        "role": "assistant",
                        "content": full_response
                    })
                
                print()  # Extra line for spacing


if __name__ == "__main__":
    asyncio.run(main())
